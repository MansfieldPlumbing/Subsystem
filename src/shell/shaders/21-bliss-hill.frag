precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float noise(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float smoothNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = noise(i);
    float b = noise(i + vec2(1.0, 0.0));
    float c = noise(i + vec2(0.0, 1.0));
    float d = noise(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * smoothNoise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Sky gradient
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.6, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Sun
    vec2 sunPos = vec2(0.6, 0.7);
    float sun = 0.015 / length(uv - sunPos);
    color += vec3(1.0, 0.9, 0.6) * sun;

    // Clouds
    float cloudMove = u_time * 0.05;
    float clouds = fbm(uv * 3.0 + vec2(cloudMove, 0.0));
    color = mix(color, vec3(1.0), smoothstep(0.5, 0.8, clouds) * 0.4);

    // Hills
    for (float i = 1.0; i <= 3.0; i++) {
        float speed = i * 0.2;
        float height = 0.2 * i;
        float h = 0.3 + height * sin(uv.x * 2.5 + i * 5.0 + u_time * 0.1) + 
                  0.1 * sin(uv.x * 8.0 - i * 2.0);
        
        // Add layer noise
        h += fbm(vec2(uv.x * 2.0 + i, u_time * 0.02)) * 0.1;
        
        float mask = smoothstep(h, h - 0.005, uv.y);
        
        vec3 hillGreen = vec3(0.1 + i * 0.1, 0.5 + i * 0.1, 0.1);
        vec3 shadow = vec3(0.0, 0.1, 0.0);
        
        // Grass texture detail
        float grass = fbm(uv * vec2(20.0, 40.0) + i);
        hillGreen = mix(hillGreen, hillGreen * 1.2, grass * 0.3);
        
        color = mix(color, hillGreen, mask);
    }

    // Flowers (particles)
    vec2 gv = uv * 40.0;
    vec2 id = floor(gv);
    float flowerRand = noise(id);
    if (flowerRand > 0.95) {
        float hillH = 0.3 + 0.6 * sin(id.x * 0.06 + 3.0 * 5.0 + u_time * 0.1); 
        if (uv.y < hillH) {
            vec2 fPos = fract(gv) - 0.5;
            float dist = length(fPos);
            float flower = smoothstep(0.3, 0.1, dist);
            color = mix(color, vec3(1.0, 0.9, 0.0), flower * 0.8);
        }
    }

    gl_FragColor = vec4(color, 1.0);
}
