precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), f.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Eclipse Timing
    float cycle = sin(u_time * 0.3);
    float eclipseFactor = smoothstep(0.1, 0.0, abs(cycle));
    
    // Sky
    vec3 skyDay = mix(vec3(0.2, 0.5, 0.9), vec3(0.6, 0.8, 1.0), uv.y);
    vec3 skyEclipse = mix(vec3(0.02, 0.02, 0.1), vec3(0.1, 0.05, 0.2), uv.y);
    vec3 skyColor = mix(skyDay, skyEclipse, smoothstep(-0.5, 0.8, abs(cycle * -1.0 + 0.5)));

    // Sun and Corona
    vec2 sunPos = vec2(0.7 * aspect, 0.75);
    vec2 moonPos = sunPos + vec2(cycle * 0.15, 0.0);
    float distSun = length(p - sunPos);
    float distMoon = length(p - moonPos);
    
    float corona = 0.01 / (distSun * 0.5);
    corona += 0.005 / pow(distSun, 1.5);
    vec3 coronaColor = vec3(1.0, 0.95, 0.8) * corona * (0.8 + 0.5 * eclipseFactor);
    
    float sunDisk = smoothstep(0.1, 0.095, distSun);
    float moonDisk = smoothstep(0.1, 0.105, distMoon);
    
    vec3 sunFinal = mix(vec3(0.0), vec3(1.0, 1.0, 0.9), sunDisk);
    sunFinal = mix(sunFinal, vec3(0.0), 1.0 - moonDisk);
    
    // Hills
    float hill1 = 0.3 + 0.12 * sin(p.x * 2.5 + 1.5) + 0.05 * cos(p.x * 4.0);
    float hill2 = 0.2 + 0.15 * sin(p.x * 1.8 + 4.0) + 0.08 * noise(vec2(p.x * 2.0, u_time * 0.05));
    
    vec3 hillColor1 = mix(vec3(0.1, 0.4, 0.0), vec3(0.2, 0.7, 0.1), uv.y * 2.0);
    vec3 hillColor2 = mix(vec3(0.05, 0.3, 0.0), vec3(0.3, 0.8, 0.2), uv.y * 2.5);
    
    // Eclipse lighting on hills
    vec3 hillEclipse = vec3(0.02, 0.1, 0.05);
    hillColor1 = mix(hillColor1, hillEclipse, smoothstep(0.2, 1.0, 1.0 - abs(cycle)));
    hillColor2 = mix(hillColor2, hillEclipse * 0.8, smoothstep(0.2, 1.0, 1.0 - abs(cycle)));

    // Clouds
    float n = noise(p * 3.0 + u_time * 0.05);
    vec3 cloudColor = mix(skyColor, vec3(1.0), n * 0.4 * smoothstep(0.4, 0.8, uv.y));
    skyColor = mix(skyColor, cloudColor, n * 0.5);

    // Composite
    vec3 scene = skyColor + coronaColor;
    scene = mix(scene, sunFinal, sunDisk * moonDisk);
    
    // Apply hills
    if (uv.y < hill1) {
        float shadow = smoothstep(0.4, 0.0, length(p - sunPos));
        scene = hillColor1 + shadow * 0.1 * eclipseFactor;
    }
    if (uv.y < hill2) {
        scene = hillColor2;
    }

    // Atmosphere / Vignette
    float vig = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 15.0;
    scene *= pow(vig, 0.1 + 0.2 * eclipseFactor);
    
    // Total Eclipse flash
    float flash = exp(-100.0 * abs(cycle));
    scene += vec3(0.7, 0.8, 1.0) * flash;

    gl_FragColor = vec4(scene, 1.0);
}
