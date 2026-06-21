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
    vec2 p = (gl_FragCoord.xy - 0.5 * u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Bliss XP Hill base
    float hill = sin(uv.x * 3.0 + 0.5) * 0.15 + 0.3;
    float grassMask = step(uv.y, hill + 0.05 * sin(uv.x * 10.0 + u_time * 0.2));
    
    // Sky Colors
    vec3 skyTop = vec3(0.05, 0.1, 0.2);
    vec3 skyBottom = vec3(0.1, 0.2, 0.4);
    vec3 skyColor = mix(skyBottom, skyTop, uv.y);
    
    // Stormy clouds
    float cloudNoise = fbm(p * 2.0 + u_time * 0.1);
    skyColor += vec3(cloudNoise * 0.2);

    // Lightning logic
    float lightningTrigger = step(0.98, fract(sin(u_time * 1.5) * 43758.5));
    float lightningStrike = 0.0;
    
    if (lightningTrigger > 0.5) {
        vec2 lp = p;
        float boltX = (hash(vec2(floor(u_time * 10.0))) - 0.5) * 1.5;
        lp.x -= boltX;
        float bolt = abs(lp.x + (fbm(lp * 5.0 + u_time) - 0.5) * 0.4);
        lightningStrike = smoothstep(0.03, 0.0, bolt);
        skyColor += vec3(0.7, 0.8, 1.0) * lightningStrike * 2.0;
    }

    // Grass color
    vec3 grassColor = vec3(0.1, 0.35, 0.1);
    grassColor = mix(grassColor, vec3(0.05, 0.2, 0.05), noise(uv * 15.0));
    
    // Lightning flash on grass
    grassColor += lightningStrike * lightningTrigger * vec3(0.2, 0.3, 0.5);

    // Final composition
    vec3 color = mix(skyColor, grassColor, grassMask);
    
    // Rain
    float rain = fract(sin(uv.x * 50.0 + uv.y * 10.0 + u_time * 20.0) * 43758.5);
    if(rain > 0.98 && grassMask < 0.5) {
        color += 0.15;
    }

    gl_FragColor = vec4(color, 1.0);
}
