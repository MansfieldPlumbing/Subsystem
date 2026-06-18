precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = uv * 2.0 - 1.0;
    p.x *= u_resolution.x / u_resolution.y;

    // Horizon line
    float horizon = -0.2 + sin(p.x * 0.5 + u_time * 0.2) * 0.02;
    
    // Background sky gradient
    vec3 skyTop = vec3(0.0, 0.45, 0.85);
    vec3 skyBottom = vec3(0.6, 0.8, 1.0);
    vec3 sky = mix(skyBottom, skyTop, pow(uv.y, 0.7));

    // Sun/Atmospheric glow
    float sun = 0.005 / length(p - vec2(0.8, 0.5));
    sky += vec3(1.0, 0.9, 0.6) * sun;

    // Rolling Green Hills
    float hill1 = -0.4 + sin(p.x * 1.5 + 1.0) * 0.2 + cos(p.x * 0.8) * 0.1;
    float hill2 = -0.6 + cos(p.x * 1.2 + u_time * 0.1) * 0.15 + sin(p.x * 2.5) * 0.05;
    float hill3 = -0.8 + sin(p.x * 0.5 - u_time * 0.05) * 0.25;

    vec3 grassColor1 = vec3(0.1, 0.5, 0.1);
    vec3 grassColor2 = vec3(0.2, 0.7, 0.2);
    vec3 grassColor3 = vec3(0.05, 0.3, 0.05);

    vec3 finalColor = sky;

    // Layers with simplistic shading
    if (p.y < hill1) {
        finalColor = mix(grassColor1, grassColor2, (p.y - hill1 + 0.5));
    }
    if (p.y < hill2) {
        finalColor = mix(grassColor2, grassColor1, (p.y - hill2 + 0.4));
    }
    if (p.y < hill3) {
        finalColor = mix(grassColor3, grassColor2, (p.y - hill3 + 0.6));
    }

    // Fluffy Clouds
    for(float i = 0.0; i < 5.0; i++) {
        float speed = (i + 1.0) * 0.05;
        vec2 cloudPos = vec2(fract(0.2 * i + u_time * speed) * 3.0 - 1.5, 0.4 + 0.1 * i);
        float d = length((p - cloudPos) * vec2(1.5, 3.0));
        float cloudShape = smoothstep(0.2, 0.0, d);
        finalColor = mix(finalColor, vec3(1.0), cloudShape * 0.8);
    }

    // Subtle vignette
    float vignette = uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y);
    finalColor *= pow(vignette * 15.0, 0.1);

    gl_FragColor = vec4(finalColor, 1.0);
}
