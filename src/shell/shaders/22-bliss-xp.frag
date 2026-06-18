precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Background Gradient (XP Bliss-inspired palette)
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.6, 0.8, 1.0);
    vec3 hillGreen = vec3(0.1, 0.6, 0.2);
    vec3 hillDark = vec3(0.05, 0.3, 0.1);

    // Rolling hill logic using sine waves
    float wave1 = sin(p.x * 1.5 + u_time * 0.2) * 0.2;
    float wave2 = cos(p.x * 2.5 - u_time * 0.1) * 0.15;
    float hillLine = -0.3 + wave1 + wave2;

    // Mask for sky vs hills
    float hillMask = smoothstep(hillLine, hillLine - 0.01, p.y);
    
    // Sky color with moving clouds
    vec3 skyColor = mix(skyBottom, skyTop, uv.y + 0.2);
    
    // Simple procedural clouds
    float cloud = 0.0;
    cloud += sin(p.x * 4.0 + u_time * 0.1) * sin(p.y * 8.0);
    cloud += cos(p.x * 2.0 - u_time * 0.05) * 0.5;
    skyColor += smoothstep(0.4, 1.0, cloud) * 0.3;

    // Grass texture/shading
    float grassPattern = sin(p.x * 40.0) * cos(p.y * 40.0 + u_time);
    vec3 grassColor = mix(hillDark, hillGreen, (p.y - hillLine + 0.5) + grassPattern * 0.05);
    
    // Sun glow
    vec2 sunPos = vec2(0.6, 0.6);
    float sun = 0.03 / distance(p, sunPos);
    skyColor += vec3(1.0, 0.9, 0.6) * sun;

    // Final color mix
    vec3 finalColor = mix(skyColor, grassColor, hillMask);

    // Subtle vignette
    finalColor *= 1.0 - dot(p, p) * 0.15;

    gl_FragColor = vec4(finalColor, 1.0);
}
