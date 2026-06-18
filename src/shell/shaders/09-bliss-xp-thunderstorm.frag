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
    vec2 p = uv * 2.0 - 1.0;
    p.x *= u_resolution.x / u_resolution.y;

    // Bliss XP Gradient Base
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.4, 0.7, 1.0);
    vec3 grassColor = vec3(0.1, 0.5, 0.1);
    
    // Rolling Hill Logic
    float hill = sin(p.x * 1.5 + 0.5) * 0.2 - 0.4;
    float hillMask = smoothstep(hill, hill - 0.01, p.y);
    
    // Stormy Clouds overlay
    float cloudDensity = fbm(p * 2.0 + u_time * 0.1);
    float stormFactor = sin(u_time * 0.5) * 0.5 + 0.5;
    vec3 stormColor = vec3(0.1, 0.1, 0.2);
    
    // Background sky blend
    vec3 sky = mix(skyBottom, skyTop, uv.y);
    sky = mix(sky, stormColor, cloudDensity * stormFactor * 0.8);

    // Lightning strike
    float lightning = 0.0;
    float strikeTime = fract(u_time * 0.4);
    if (strikeTime > 0.8) {
        float strikeX = (hash(vec2(floor(u_time * 5.0))) - 0.5) * 2.0;
        float bolt = abs(p.x - strikeX + sin(p.y * 10.0 + u_time * 20.0) * 0.05);
        lightning = smoothstep(0.04, 0.0, bolt) * step(0.1, p.y - hill);
        lightning *= hash(vec2(u_time)) * 2.0;
    }
    
    // Final color assembly
    vec3 finalColor = mix(sky, grassColor * (0.6 + 0.4 * noise(p * 10.0)), hillMask);
    
    // Add lightning flash to the whole scene
    finalColor += lightning * vec3(0.8, 0.8, 1.0);
    
    // Rain effect
    float rain = fract(sin(dot(uv + vec2(0.0, u_time * 0.5), vec2(12.9898, 78.233))) * 43758.5453);
    if (rain > 0.98) finalColor += 0.1;

    gl_FragColor = vec4(finalColor, 1.0);
}
