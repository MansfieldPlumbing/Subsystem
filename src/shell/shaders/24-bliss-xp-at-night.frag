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
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Night Sky Colors
    vec3 spaceColor = vec3(0.02, 0.05, 0.15);
    vec3 horizonColor = vec3(0.1, 0.05, 0.2);
    
    // Rolling Hills geometry
    float hill1 = 0.3 * sin(uv.x * 3.0 + 1.0) + 0.1 * sin(uv.x * 7.0) - 0.4;
    float hill2 = 0.25 * cos(uv.x * 2.5 - 0.5) + 0.15 * sin(uv.x * 5.0) - 0.6;
    
    // Background - Night Sky
    vec3 color = mix(horizonColor, spaceColor, uv.y);
    
    // Stars
    float starLayer = pow(hash(uv * 50.0 + floor(u_time * 0.1)), 100.0);
    color += starLayer * (0.5 + 0.5 * sin(u_time + uv.x * 10.0));

    // Aurora/Mist effect
    float aurora = fbm(vec2(uv.x * 2.0 + u_time * 0.05, uv.y * 3.0));
    color += vec3(0.0, 0.1, 0.05) * aurora * smoothstep(0.3, 0.8, uv.y);

    // Hill 1 (Deep Blue-Green)
    float mask1 = smoothstep(hill1, hill1 - 0.005, uv.y - 0.5);
    vec3 hill1Color = vec3(0.02, 0.15, 0.08);
    hill1Color *= (0.6 + 0.4 * fbm(uv * 8.0)); // Texture
    hill1Color *= (uv.y + 0.5); // Depth
    color = mix(color, hill1Color, mask1);

    // Hill 2 (Foreground Darker Green)
    float mask2 = smoothstep(hill2, hill2 - 0.005, uv.y - 0.4);
    vec3 hill2Color = vec3(0.01, 0.1, 0.05);
    hill2Color *= (0.5 + 0.5 * fbm(uv * 12.0)); // Fine texture
    hill2Color *= (uv.y + 0.4); 
    color = mix(color, hill2Color, mask2);

    // Moon Glow
    vec2 moonPos = vec2(0.8, 0.8);
    float dist = length(uv - moonPos);
    float moon = smoothstep(0.08, 0.075, dist);
    float glow = exp(-dist * 10.0);
    color += vec3(0.8, 0.9, 1.0) * glow * 0.4;
    color = mix(color, vec3(0.95, 0.95, 1.0), moon);

    // Vignette
    color *= 1.0 - 0.4 * length(uv - 0.5);

    gl_FragColor = vec4(color, 1.0);
}
