//------------------------------------------------------------------------------
// Tone-mapping configuration
//------------------------------------------------------------------------------

// Operators for LDR output
#define TONE_MAPPING_UNREAL           0
#define TONE_MAPPING_FILMIC_ALU       1
#define TONE_MAPPING_LINEAR           2 // Operators with built-in sRGB go above
#define TONE_MAPPING_REINHARD         3
#define TONE_MAPPING_ACES_SRGB        4
#define TONE_MAPPING_ACES             5

// Debug operators
#define TONE_MAPPING_DISPLAY_RANGE    9

#ifdef TARGET_MOBILE
    #define TONE_MAPPING_OPERATOR     TONE_MAPPING_UNREAL
#else
    #define TONE_MAPPING_OPERATOR     TONE_MAPPING_ACES
#endif

// When defined, the ACES tone mapper will match the brightness of the "ACES sRGB" tone mapper
// It is *not* correct, but it helps for compatibility
#define TONEMAP_ACES_MATCH_BRIGHTNESS

//------------------------------------------------------------------------------
// Tone-mapping operators for LDR output
//------------------------------------------------------------------------------

vec3 Tonemap_Linear(const vec3 x) {
    return x;
}

vec3 Tonemap_Reinhard(const vec3 x) {
    // Reinhard et al. 2002, "Photographic Tone Reproduction for Digital Images", Eq. 3
    return x / (1.0 + luminance(x));
}

vec3 Tonemap_Unreal(const vec3 x) {
    // Unreal, Documentation: "Color Grading"
    // Adapted to be close to Tonemap_ACES, with similar range
    // Gamma 2.2 correction is baked in, don't use with sRGB conversion!
    return x / (x + 0.155) * 1.019;
}

vec3 Tonemap_FilmicALU(const vec3 x) {
    // Hable 2010, "Filmic Tonemapping Operators"
    // Based on Duiker's curve, optimized by Hejl and Burgess-Dawson
    // Gamma 2.2 correction is baked in, don't use with sRGB conversion!
    vec3 c = max(vec3(0.0), x - 0.004);
    return (c * (c * 6.2 + 0.5)) / (c * (c * 6.2 + 1.7) + 0.06);
}

vec3 Tonemap_ACES_sRGB(const vec3 x) {
    // Narkowicz 2015, "ACES Filmic Tone Mapping Curve"
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return (x * (a * x + b)) / (x * (c * x + d) + e);
}

const mat3 sRGB_2_AP0 = mat3(
     0.4397010000,  0.0897923000,  0.0175440000,
     0.3829780000,  0.8134230000,  0.1115440000,
     0.1773350000,  0.0967616000,  0.8707040000
);

const mat3 AP0_2_AP1 = mat3(
     1.4514393161, -0.0765537734,  0.0083161484,
    -0.2365107469,  1.1762296998, -0.0060324498,
    -0.2149285693, -0.0996759264,  0.9977163014
);

const mat3 AP1_2_AP0 = mat3(
     0.6954522414,  0.0447945634, -0.0055258826,
     0.1406786965,  0.8596711185,  0.0040252103,
     0.1638690622,  0.0955343182,  1.0015006723
);

const mat3 AP1_2_XYZ = mat3(
     0.6624541811,  0.2722287168, -0.0055746495,
     0.1340042065,  0.6740817658,  0.0040607335,
     0.1561876870,  0.0536895174,  1.0103391003
);

const mat3 XYZ_2_AP1 = mat3(
     1.6410233797, -0.6636628587,  0.0117218943,
    -0.3248032942,  1.6153315917, -0.0082844420,
    -0.2364246952,  0.0167563477,  0.9883948585
);

const mat3 AP1_2_sRGB = mat3(
     1.7050500000, -0.1302600000, -0.0240000000,
    -0.6217900000,  1.1408000000, -0.1289700000,
    -0.0832600000, -0.0105500000,  1.1529700000
);

const vec3 AP1_RGB2Y = vec3(0.272229, 0.674082, 0.0536895);

float rgb_2_saturation(vec3 rgb) {
    // Input:  ACES
    // Output: OCES
    const float TINY = 1e-5;
    float mi = min3(rgb);
    float ma = max3(rgb);
    return (max(ma, TINY) - max(mi, TINY)) / max(ma, 1e-2);
}

float rgb_2_yc(vec3 rgb) {
    const float ycRadiusWeight = 1.75;

    // Converts RGB to a luminance proxy, here called YC
    // YC is ~ Y + K * Chroma
    // Constant YC is a cone-shaped surface in RGB space, with the tip on the
    // neutral axis, towards white.
    // YC is normalized: RGB 1 1 1 maps to YC = 1
    //
    // ycRadiusWeight defaults to 1.75, although can be overridden in function
    // call to rgb_2_yc
    // ycRadiusWeight = 1 -> YC for pure cyan, magenta, yellow == YC for neutral
    // of same value
    // ycRadiusWeight = 2 -> YC for pure red, green, blue  == YC for  neutral of
    // same value.

    float r = rgb.r;
    float g = rgb.g;
    float b = rgb.b;

    float chroma = sqrt(b* (b - g) + g * (g - r) + r * (r - b));

    return (b + g + r + ycRadiusWeight * chroma) / 3.0;
}

float sigmoid_shaper(float x) {
    // Sigmoid function in the range 0 to 1 spanning -2 to +2.
    float t = max(1.0 - abs(x / 2.0), 0.0);
    float y = 1.0 + sign(x) * (1.0 - t * t);

    return y / 2.0;
}

float glow_fwd(float ycIn, float glowGainIn, float glowMid) {
    float glowGainOut;

    if (ycIn <= 2.0 / 3.0 * glowMid) {
        glowGainOut = glowGainIn;
    } else if ( ycIn >= 2.0 * glowMid) {
        glowGainOut = 0.0;
    } else {
        glowGainOut = glowGainIn * (glowMid / ycIn - 1.0 / 2.0);
    }

    return glowGainOut;
}

float rgb_2_hue(vec3 rgb) {
    // Returns a geometric hue angle in degrees (0-360) based on RGB values.
    // For neutral colors, hue is undefined and the function will return a quiet NaN value.
    float hue;
    if (rgb.x == rgb.y && rgb.y == rgb.z) {
        hue = 0.0;// RGB triplets where RGB are equal have an undefined hue
    } else {
        hue = (180.0 / PI) * atan2(sqrt(3.0) * (rgb.y - rgb.z), 2.0 * rgb.x - rgb.y - rgb.z);
    }

    if (hue < 0.0) hue = hue + 360.0;

    return hue;
}

float center_hue(float hue, float centerH) {
    float hueCentered = hue - centerH;
    if (hueCentered < -180.0) hueCentered = hueCentered + 360.0;
    else if (hueCentered > 180.0) hueCentered = hueCentered - 360.0;
    return hueCentered;
}

vec3 XYZ_2_xyY(vec3 XYZ) {
    float divisor = max(XYZ.x + XYZ.y + XYZ.z, 1e-5);
    return vec3(XYZ.xy / divisor, XYZ.y);
}

vec3 xyY_2_XYZ(vec3 xyY) {
    float a = xyY.z / max(xyY.y, 1e-5);
    vec3 XYZ = vec3(xyY.xz, (1.0 - xyY.x - xyY.y));
    XYZ.xz *= a;
    return XYZ;
}

vec3 darkSurround_to_dimSurround(vec3 linearCV) {
    const float DIM_SURROUND_GAMMA = 0.9811;

    vec3 XYZ = AP1_2_XYZ * linearCV;

    vec3 xyY = XYZ_2_xyY(XYZ);
    xyY.z = clamp(xyY.z, 0.0, MEDIUMP_FLT_MAX);
    xyY.z = pow(xyY.z, DIM_SURROUND_GAMMA);
    XYZ = xyY_2_XYZ(xyY);

    return XYZ_2_AP1 * XYZ;
}

vec3 Tonemap_ACES(const vec3 color) {
    // From https://github.com/ampas/aces-dev
    // Some bits were removed to adapt to our desired output
    // Input:  linear sRGB
    // Output: linear sRGB

    // "Glow" module constants
    const float RRT_GLOW_GAIN = 0.05;
    const float RRT_GLOW_MID = 0.08;

    // Red modifier constants
    const float RRT_RED_SCALE = 0.82;
    const float RRT_RED_PIVOT = 0.03;
    const float RRT_RED_HUE   = 0.0;
    const float RRT_RED_WIDTH = 135.0;

    // Desaturation contants
    const float RRT_SAT_FACTOR = 0.96;
    const float ODT_SAT_FACTOR = 0.93;

    // This assumes our working color space is sRGB
    vec3 ap0 = sRGB_2_AP0 * color;

    // Glow module
    float saturation = rgb_2_saturation(ap0);
    float ycIn = rgb_2_yc(ap0);
    float s = sigmoid_shaper((saturation - 0.4) / 0.2);
    float addedGlow = 1.0 + glow_fwd(ycIn, RRT_GLOW_GAIN * s, RRT_GLOW_MID);
    ap0 *= addedGlow;

    // Red modifier
    float hue = rgb_2_hue(ap0);
    float centeredHue = center_hue(hue, RRT_RED_HUE);
    float hueWeight = sq(smoothstep(0.0, 1.0, 1.0 - abs(2.0 * centeredHue / RRT_RED_WIDTH)));

    ap0.r += hueWeight * saturation * (RRT_RED_PIVOT - ap0.r) * (1.0 - RRT_RED_SCALE);

    // ACES to RGB rendering space
    vec3 ap1 = clamp(AP0_2_AP1 * ap0, 0.0, MEDIUMP_FLT_MAX);

    // Global desaturation
    ap1 = mix(vec3(dot(ap1, AP1_RGB2Y)), ap1, RRT_SAT_FACTOR);

#if defined(TONEMAP_ACES_MATCH_BRIGHTNESS)
    ap1 *= 1.0 / 0.6;
#endif

    // Fitting of RRT + ODT (RGB monitor 100 nits dim) from:
    // https://github.com/colour-science/colour-unity/blob/master/Assets/Colour/Notebooks/CIECAM02_Unity.ipynb
    const float a = 2.785085;
    const float b = 0.107772;
    const float c = 2.936045;
    const float d = 0.887122;
    const float e = 0.806889;
    vec3 rgbPost = (ap1 * (a * ap1 + b)) / (ap1 * (c * ap1 + d) + e);

    // Apply gamma adjustment to compensate for dim surround
    vec3 linearCV = darkSurround_to_dimSurround(rgbPost);

    // Apply desaturation to compensate for luminance difference
    linearCV = mix(vec3(dot(linearCV, AP1_RGB2Y)), linearCV, ODT_SAT_FACTOR);

    // Convert to display primary encoding (Rec.709 primaries, D65 white point)
    return AP1_2_sRGB * linearCV;
}

//------------------------------------------------------------------------------
// Debug tone-mapping operators, for LDR output
//------------------------------------------------------------------------------

/**
 * Converts the input HDR RGB color into one of 16 debug colors that represent
 * the pixel's exposure. When the output is cyan, the input color represents
 * middle gray (18% exposure). Every exposure stop above or below middle gray
 * causes a color shift.
 *
 * The relationship between exposures and colors is:
 *
 * -5EV  - black
 * -4EV  - darkest blue
 * -3EV  - darker blue
 * -2EV  - dark blue
 * -1EV  - blue
 *  OEV  - cyan
 * +1EV  - dark green
 * +2EV  - green
 * +3EV  - yellow
 * +4EV  - yellow-orange
 * +5EV  - orange
 * +6EV  - bright red
 * +7EV  - red
 * +8EV  - magenta
 * +9EV  - purple
 * +10EV - white
 */
#if TONE_MAPPING_OPERATOR == TONE_MAPPING_DISPLAY_RANGE
vec3 Tonemap_DisplayRange(const vec3 x) {
    // 16 debug colors + 1 duplicated at the end for easy indexing
    const vec3 debugColors[17] = vec3[](
         vec3(0.0, 0.0, 0.0),         // black
         vec3(0.0, 0.0, 0.1647),      // darkest blue
         vec3(0.0, 0.0, 0.3647),      // darker blue
         vec3(0.0, 0.0, 0.6647),      // dark blue
         vec3(0.0, 0.0, 0.9647),      // blue
         vec3(0.0, 0.9255, 0.9255),   // cyan
         vec3(0.0, 0.5647, 0.0),      // dark green
         vec3(0.0, 0.7843, 0.0),      // green
         vec3(1.0, 1.0, 0.0),         // yellow
         vec3(0.90588, 0.75294, 0.0), // yellow-orange
         vec3(1.0, 0.5647, 0.0),      // orange
         vec3(1.0, 0.0, 0.0),         // bright red
         vec3(0.8392, 0.0, 0.0),      // red
         vec3(1.0, 0.0, 1.0),         // magenta
         vec3(0.6, 0.3333, 0.7882),   // purple
         vec3(1.0, 1.0, 1.0),         // white
         vec3(1.0, 1.0, 1.0)          // white
    );

    // The 5th color in the array (cyan) represents middle gray (18%)
    // Every stop above or below middle gray causes a color shift
    float v = log2(luminance(x) / 0.18);
    v = clamp(v + 5.0, 0.0, 15.0);
    int index = int(v);
    return mix(debugColors[index], debugColors[index + 1], v - float(index));
}
#endif

//------------------------------------------------------------------------------
// Tone-mapping dispatch
//------------------------------------------------------------------------------

/**
 * Tone-maps the specified RGB color. The input color must be in linear HDR and
 * pre-exposed. Our HDR to LDR tone mapping operators are designed to tone-map
 * the range [0..~8] to [0..1].
 */
vec3 tonemap(const vec3 x) {
#if TONE_MAPPING_OPERATOR == TONE_MAPPING_UNREAL
    return Tonemap_Unreal(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_FILMIC_ALU
    return Tonemap_FilmicALU(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_LINEAR
    return Tonemap_Linear(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_REINHARD
    return Tonemap_Reinhard(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_ACES_SRGB
    return Tonemap_ACES_sRGB(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_ACES
    return Tonemap_ACES(x);
#elif TONE_MAPPING_OPERATOR == TONE_MAPPING_DISPLAY_RANGE
    return Tonemap_DisplayRange(x);
#endif
}

//------------------------------------------------------------------------------
// Processing tone-mappers
//------------------------------------------------------------------------------

vec3 Tonemap_ReinhardWeighted(const vec3 x, float weight) {
    // Weighted Reinhard tone-mapping operator designed for post-processing
    // This tone-mapping operator is invertible
    return x * (weight / (max3(x) + 1.0));
}

vec3 Tonemap_ReinhardWeighted_Invert(const vec3 x) {
    // Inverse Reinhard tone-mapping operator, designed to be used in conjunction
    // with the weighted Reinhard tone-mapping operator
    return x / (1.0 - max3(x));
}
