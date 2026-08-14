#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "DirectXTex/DirectXTex.h"

// Initializes the lookup tables used by bc7enc and rgbcx. Call once before
// CompressWithBcEncoder, while no encoder worker threads are running.
void InitializeBcEncoder() noexcept;

// Returns true for the unsigned BC formats implemented by bc7enc/rgbcx.
bool IsBcEncoderFormat(DXGI_FORMAT format) noexcept;

// Compresses one image using bc7enc/rgbcx. S_FALSE means the image uses a
// feature the new encoder does not support and should be sent to DirectXTex.
HRESULT CompressWithBcEncoder(
	const DirectX::Image& sourceImage,
	DXGI_FORMAT outputFormat,
	DirectX::ScratchImage& outputImage) noexcept;
