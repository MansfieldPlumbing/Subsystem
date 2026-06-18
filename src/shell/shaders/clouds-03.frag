precision mediump float;
uniform vec2 u_resolution;
uniform float u_time;

// Noise function from Inigo Quilez
float hash( vec2 p )
{
    p = vec2( dot(p,vec2(127.1,311.7)), dot(p,vec2(269.5,183.3)) );
    return -1.0 + 2.0 * fract(sin(p.x)*sin(p.y)*43758.5453123);
}

float noise( vec2 p )
{
    vec2 i = floor( p );
    vec2 f = fract( p );

    vec2 u = f*f*(3.0-2.0*f);

    return mix( mix( hash( i + vec2(0.0,0.0) ),
                    hash( i + vec2(1.0,0.0) ), u.x),
                mix( hash( i + vec2(0.0,1.0) ),
                    hash( i + vec2(1.0,1.0) ), u.x), u.y);
}

// Fractal Brownian Motion (FBM)
float fbm(vec2 p) {
    float sum = 0.0;
    float freq = 2.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++) {
        sum += noise(p * freq) * amp;
        freq *= 2.0;
        amp *= 0.5;
    }
    return sum;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution;
    uv.x *= u_resolution.x / u_resolution.y; // Aspect ratio correction

    vec2 p = uv * 4.0; // Scale the coordinates
    p.x += u_time * 0.1; // Make clouds move

    float cloudDensity = fbm(p + fbm(p * 0.5 + u_time * 0.05) * 0.5); // Layer FBM for more organic look
    cloudDensity = clamp(cloudDensity * 1.5 - 0.2, 0.0, 1.0); // Adjust contrast and brightness

    // Simple sky color gradient
    vec3 skyColor = mix(vec3(0.3, 0.5, 0.8), vec3(0.6, 0.8, 1.0), uv.y);

    // Cloud color
    vec3 cloudColor = vec3(0.9, 0.9, 0.95);

    // Mix sky and cloud colors based on density
    vec3 finalColor = mix(skyColor, cloudColor, cloudDensity);

    // Add some "light scattering" effect (bluish tint in thicker parts)
    finalColor = mix(finalColor, vec3(0.7, 0.8, 0.9), cloudDensity * cloudDensity * 0.5);


    gl_FragColor = vec4(finalColor, 1.0);
}
