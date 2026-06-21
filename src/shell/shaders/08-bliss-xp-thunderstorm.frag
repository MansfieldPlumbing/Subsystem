precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * noise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Time variables
    float t = u_time * 0.5;
    float stormCycle = sin(t * 0.2) * 0.5 + 0.5; // 0 is bliss, 1 is storm
    
    // Lightning trigger
    float lightning = 0.0;
    if (stormCycle > 0.6) {
        float burst = fract(u_time * 1.5);
        if (burst > 0.9) {
            float strike = hash(vec2(floor(u_time * 10.0), 0.0));
            if (strike > 0.7) {
                float bolt = abs(p.x - strike * aspect + (noise(vec2(p.y * 10.0, u_time)) - 0.5) * 0.1);
                lightning = smoothstep(0.02, 0.0, bolt) * (1.0 - uv.y);
                lightning += step(0.98, burst) * 0.3; // Flash
            }
        }
    }

    // Sky
    vec3 blissSky = mix(vec3(0.2, 0.5, 0.9), vec3(0.6, 0.8, 1.0), uv.y);
    vec3 stormSky = mix(vec3(0.05, 0.05, 0.1), vec3(0.2, 0.2, 0.3), uv.y);
    vec3 skyColor = mix(blissSky, stormSky, stormCycle);
    
    // Clouds
    float cloudNoise = fbm(p * 2.0 + vec2(u_time * 0.05, 0.0));
    float cloudMask = smoothstep(0.4, 0.7, cloudNoise);
    vec3 cloudColor = mix(vec3(1.0), vec3(0.2, 0.2, 0.25), stormCycle);
    skyColor = mix(skyColor, cloudColor, cloudMask * (1.0 - uv.y * 0.5));

    // Rolling Hills
    float hill1Y = 0.3 + 0.15 * sin(p.x * 1.2 + 1.0) + 0.05 * noise(vec2(p.x * 3.0, 0.0));
    float hill2Y = 0.2 + 0.1 * sin(p.x * 0.8 - 0.5) + 0.03 * noise(vec2(p.x * 4.0, 1.0));
    
    vec3 finalColor = skyColor;

    // Hill 1 (Back)
    if (uv.y < hill1Y) {
        vec3 hill1Bliss = vec3(0.1, 0.5, 0.1) * (uv.y / hill1Y + 0.5);
        vec3 hill1Storm = vec3(0.02, 0.15, 0.05);
        finalColor = mix(hill1Bliss, hill1Storm, stormCycle);
        finalColor += lightning * 0.4;
    }

    // Hill 2 (Front)
    if (uv.y < hill2Y) {
        vec2 hillUV = vec2(p.x * 5.0, uv.y * 10.0);
        float grass = noise(hillUV + u_time * 0.1);
        vec3 hill2Bliss = vec3(0.2, 0.7, 0.1) * (0.8 + 0.2 * grass);
        vec3 hill2Storm = vec3(0.05, 0.2, 0.1);
        finalColor = mix(hill2Bliss, hill2Storm, stormCycle);
        finalColor += lightning * 0.6;
    }

    // Rain
    if (stormCycle > 0.4) {
        float rainVel = u_time * 4.0;
        float rain = fract(sin(dot(vec2(p.x, p.y + rainVel), vec2(12.9898, 78.233))) * 43758.5453);
        float rainMask = smoothstep(0.95, 1.0, rain) * stormCycle;
        finalColor = mix(finalColor, vec3(0.7, 0.7, 0.8), rainMask * 0.4);
    }

    // Lightning Flash impact on whole screen
    finalColor += lightning * vec3(0.8, 0.8, 1.0) * 0.5;

    // Vignette
    float vignette = 1.0 - smoothstep(0.5, 1.5, length(uv - 0.5));
    finalColor *= mix(1.0, vignette, stormCycle * 0.5);

    gl_FragColor = vec4(finalColor, 1.0);
}
