// 駒・盤の線・枠など、ふつうの（不透明な）物を描く。光の向きを決め打ちした簡単な明暗だけを付ける。
// Resources に置いてあるので、Web（WebGL）版にも必ず含まれる（Unity 既定のマテリアルは Web 版に含まれずピンクになった）
Shader "Shogi3D/Solid"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _BaseColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 右上手前から光が当たっているように、面の向きで明るさを変える
                float3 n = normalize(i.normal);
                float light = 0.6 + 0.4 * saturate(dot(n, normalize(float3(0.35, 0.85, -0.4))));
                return fixed4(_BaseColor.rgb * light, 1);
            }
            ENDCG
        }
    }
}
