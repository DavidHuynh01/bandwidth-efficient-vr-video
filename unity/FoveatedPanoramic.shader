Shader "Skybox/FoveatedPanoramic"
{
    Properties
    {
        _MainTex ("Detail (tiles)", 2D) = "black" {}
        _BaseTex ("Base pano", 2D) = "grey" {}
        _Tint ("Tint", Color) = (0.5, 0.5, 0.5, 1)
        [Gamma] _Exposure ("Exposure", Range(0, 4)) = 1.0
        _GazeDir ("Gaze direction", Vector) = (0, 0, 1, 0)
        _BlurStrength ("Blur strength", Range(0, 0.06)) = 0.02
        _FocusAngle ("Sharp cone deg", Range(0, 90)) = 20
        _FalloffAngle ("Blur falloff deg", Range(1, 120)) = 60
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _BaseTex;
            half4 _Tint;
            half _Exposure;
            float4 _GazeDir;
            float _BlurStrength;
            float _FocusAngle;
            float _FalloffAngle;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            // same lat/long unwrap as unity's built-in panoramic skybox, so the
            // tiles and the base layer land exactly where we expect them to
            float2 ToEquirect(float3 d)
            {
                float3 n = normalize(d);
                float lat = acos(n.y);
                float lon = atan2(n.z, n.x);
                float2 uv = float2(lon, lat) * float2(0.5 / UNITY_PI, 1.0 / UNITY_PI);
                return float2(0.5, 1.0) - uv;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float2 uv = ToEquirect(dir);

                // how far this pixel is from the gaze, eased so the blur ramps
                // in smoothly rather than with a hard linear edge
                float ang = degrees(acos(clamp(dot(dir, normalize(_GazeDir.xyz)), -1.0, 1.0)));
                float t = smoothstep(0.0, 1.0, saturate((ang - _FocusAngle) / _FalloffAngle));
                float r = t * _BlurStrength;

                // round disc blur: two rings of samples around the centre, inner
                // ring weighted heavier. looks like real defocus, no box streaks.
                // x is squashed by half so the disc stays round on the 2:1 map.
                fixed3 baseCol = tex2D(_BaseTex, uv).rgb;
                float wsum = 1.0;
                [unroll] for (int k = 0; k < 12; k++)
                {
                    float a = (k % 6) * 1.0471976 + ((k < 6) ? 0.0 : 0.5236);
                    float ring = (k < 6) ? 1.0 : 0.5;
                    float wk = (k < 6) ? 0.6 : 1.0;
                    float2 off = float2(cos(a), sin(a)) * ring * r * float2(0.5, 1.0);
                    baseCol += tex2D(_BaseTex, uv + off).rgb * wk;
                    wsum += wk;
                }
                baseCol /= wsum;

                // sharp tiles near the gaze, fading into the blurred base as we
                // move out, so the edge of the sharp area isn't a hard rectangle
                fixed4 detail = tex2D(_MainTex, uv);
                fixed3 col = lerp(baseCol, detail.rgb, detail.a * (1.0 - t));

                col *= _Tint.rgb * unity_ColorSpaceDouble.rgb * _Exposure;
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
}
