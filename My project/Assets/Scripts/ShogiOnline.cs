// オンライン対戦の通信。Unity の Relay（中継サーバー）を通して、相手と「指した手」だけをやりとりする
//   部屋を作る人（ホスト）: Relay に部屋を作り、参加コードを相手に伝える
//   参加する人: 参加コードで部屋に入る
// ブラウザ（WebGL）でも PC でもつながるように、通信は WebSocket（wss）を使う
// 使うパッケージ: Multiplayer Services（Relay・Authentication を含む）、Unity Transport

using System;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

public class ShogiOnline : IDisposable
{
    enum Msg { Start = 1, Move = 2, Resign = 3 }

    public bool IsHost { get; private set; }
    public string JoinCode { get; private set; }
    public bool Connected => connection.IsCreated;

    public event Action PeerConnected;                  // 相手とつながった
    public event Action Disconnected;                   // 相手との接続が切れた
    public event Action<bool> StartReceived;            // 対局開始（true なら部屋を作った人が先手）
    public event Action<int, int, int, bool, int> MoveReceived; // from, to, 打つ駒の種類, 成り, その手の前の手数
    public event Action ResignReceived;                 // 相手が投了した

    NetworkDriver driver;
    NetworkPipeline pipeline;
    NetworkConnection connection;

    // Unity のサービスに名前なし（匿名）でログインする。初回だけ時間がかかる
    static async Task SignInAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    // 部屋を作る（相手 1 人分の席を Relay に用意して、参加コードをもらう）
    public static async Task<ShogiOnline> HostAsync()
    {
        await SignInAsync();
        var allocation = await RelayService.Instance.CreateAllocationAsync(1);
        string code = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
        var relayData = AllocationUtils.ToRelayServerData(allocation, "wss");

        var online = new ShogiOnline { IsHost = true, JoinCode = code };
        online.CreateDriver(ref relayData);
        if (online.driver.Bind(NetworkEndpoint.AnyIpv4) != 0 || online.driver.Listen() != 0)
        {
            online.Dispose();
            throw new Exception("部屋の準備に失敗しました");
        }
        return online;
    }

    // 参加コードで部屋に入る
    public static async Task<ShogiOnline> JoinAsync(string code)
    {
        await SignInAsync();
        code = code.Trim().ToUpperInvariant();
        var allocation = await RelayService.Instance.JoinAllocationAsync(code);
        var relayData = AllocationUtils.ToRelayServerData(allocation, "wss");

        var online = new ShogiOnline { IsHost = false, JoinCode = code };
        online.CreateDriver(ref relayData);
        if (online.driver.Bind(NetworkEndpoint.AnyIpv4) != 0)
        {
            online.Dispose();
            throw new Exception("接続の準備に失敗しました");
        }
        online.connection = online.driver.Connect(relayData.Endpoint);
        return online;
    }

    void CreateDriver(ref RelayServerData relayData)
    {
        var settings = new NetworkSettings();
        settings.WithRelayParameters(ref relayData);
        driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
        pipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage)); // 順番どおり確実に届ける
    }

    // 毎フレーム呼ぶ（受信と、Relay との接続の維持）
    public void Update()
    {
        if (!driver.IsCreated) return;
        driver.ScheduleUpdate().Complete();

        if (IsHost)
        {
            NetworkConnection c;
            while ((c = driver.Accept()) != default)
            {
                if (connection.IsCreated) driver.Disconnect(c); // 3 人目は入れない
                else
                {
                    connection = c;
                    PeerConnected?.Invoke();
                }
            }
        }

        NetworkEvent.Type type;
        while ((type = driver.PopEvent(out var conn, out var reader)) != NetworkEvent.Type.Empty)
        {
            switch (type)
            {
                case NetworkEvent.Type.Connect:
                    if (!IsHost) PeerConnected?.Invoke();
                    break;
                case NetworkEvent.Type.Disconnect:
                    if (conn == connection) connection = default;
                    Disconnected?.Invoke();
                    break;
                case NetworkEvent.Type.Data:
                    switch ((Msg)reader.ReadInt())
                    {
                        case Msg.Start:
                            StartReceived?.Invoke(reader.ReadInt() == 1);
                            break;
                        case Msg.Move:
                            int from = reader.ReadInt(), to = reader.ReadInt(), drop = reader.ReadInt();
                            bool promote = reader.ReadInt() == 1;
                            int ply = reader.ReadInt();
                            MoveReceived?.Invoke(from, to, drop, promote, ply);
                            break;
                        case Msg.Resign:
                            ResignReceived?.Invoke();
                            break;
                    }
                    break;
            }
        }
    }

    public void SendStart(bool hostIsSente)
    {
        if (!Begin(out var w)) return;
        w.WriteInt((int)Msg.Start);
        w.WriteInt(hostIsSente ? 1 : 0);
        driver.EndSend(w);
    }

    public void SendMove(int from, int to, int drop, bool promote, int ply)
    {
        if (!Begin(out var w)) return;
        w.WriteInt((int)Msg.Move);
        w.WriteInt(from);
        w.WriteInt(to);
        w.WriteInt(drop);
        w.WriteInt(promote ? 1 : 0);
        w.WriteInt(ply);
        driver.EndSend(w);
    }

    public void SendResign()
    {
        if (!Begin(out var w)) return;
        w.WriteInt((int)Msg.Resign);
        driver.EndSend(w);
    }

    bool Begin(out DataStreamWriter writer)
    {
        writer = default;
        if (!driver.IsCreated || !connection.IsCreated) return false;
        return driver.BeginSend(pipeline, connection, out writer) == 0;
    }

    public void Dispose()
    {
        if (!driver.IsCreated) return;
        if (connection.IsCreated)
        {
            driver.Disconnect(connection);
            driver.ScheduleUpdate().Complete(); // 切断を相手に伝える
        }
        driver.Dispose();
        connection = default;
    }
}
