#ifndef BLUR_FS
#define BLUR_FS

#include "sh_Utils.h"

layout(location = 2) in mediump vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform m_BlurParameters
{
	mediump vec2 g_TexSize;
	int g_Radius;
	mediump float g_Sigma;
	highp vec2 g_BlurDirection;
};

layout(set = 1, binding = 0) uniform lowp texture2D m_Texture;
layout(set = 1, binding = 1) uniform lowp sampler m_Sampler;

layout(location = 0) out vec4 o_Colour;

lowp vec4 blur(int radius, highp vec2 direction, mediump vec2 texCoord, mediump vec2 texSize, mediump float sigma)
{
	mediump float invTwoSigmaSq = -0.5 / (sigma * sigma);
	highp vec2 texStep = direction / texSize;

	mediump vec4 sum = texture(sampler2D(m_Texture, m_Sampler), texCoord);
	mediump float totalFactor = 1.0;

	for (int i = 2; i <= 200; i += 2)
	{
		mediump float x = float(i) - 0.5;
		mediump float factor = exp(x * x * invTwoSigmaSq) * 2.0;
		highp vec2 offset = texStep * x;
		totalFactor += 2.0 * factor;
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord + offset) * factor;
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord - offset) * factor;
		if (i >= radius)
			break;
	}

	// this is supposed to be unnecessary with https://github.com/ppy/veldrid-spirv/pull/4,
	// but blurring is still broken on some Apple devices when removing it (at least on an M2 iPad Pro and an iPhone 12).
	// todo: investigate this.
	float one = g_BackbufferDraw ? 0 : 1;

	return sum / totalFactor * one;
}

void main(void)
{
	o_Colour = blur(g_Radius, g_BlurDirection, v_TexCoord, g_TexSize, g_Sigma);
}

#endif
