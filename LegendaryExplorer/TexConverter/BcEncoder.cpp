#include "BcEncoder.h"

#include "bc7enc/bc7enc.h"

#define RGBCX_IMPLEMENTATION
#include "bc7enc/rgbcx.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace
{
	constexpr uint32_t RGBCX_QUALITY_LEVEL = rgbcx::MAX_LEVEL;

	size_t GetBlockSize(DXGI_FORMAT format) noexcept
	{
		switch (format)
		{
		case DXGI_FORMAT_BC1_UNORM:
		case DXGI_FORMAT_BC1_UNORM_SRGB:
		case DXGI_FORMAT_BC4_UNORM:
			return 8;
		case DXGI_FORMAT_BC3_UNORM:
		case DXGI_FORMAT_BC3_UNORM_SRGB:
		case DXGI_FORMAT_BC5_UNORM:
		case DXGI_FORMAT_BC7_UNORM:
		case DXGI_FORMAT_BC7_UNORM_SRGB:
			return 16;
		default:
			return 0;
		}
	}

	bool HasNonOpaqueAlpha(const DirectX::Image& image) noexcept
	{
		for (size_t y = 0; y < image.height; ++y)
		{
			const uint8_t* pixel = image.pixels + y * image.rowPitch;
			for (size_t x = 0; x < image.width; ++x, pixel += 4)
			{
				if (pixel[3] != 255)
					return true;
			}
		}

		return false;
	}

	void CopyBlock(const DirectX::Image& image, size_t blockX, size_t blockY, uint8_t (&block)[64]) noexcept
	{
		const size_t firstX = blockX * 4;
		const size_t firstY = blockY * 4;

		for (size_t y = 0; y < 4; ++y)
		{
			const size_t sourceY = std::min(firstY + y, image.height - 1);
			for (size_t x = 0; x < 4; ++x)
			{
				const size_t sourceX = std::min(firstX + x, image.width - 1);
				const uint8_t* source = image.pixels + sourceY * image.rowPitch + sourceX * 4;
				std::memcpy(block + (y * 4 + x) * 4, source, 4);
			}
		}
	}

	void CompressBlock(
		DXGI_FORMAT format,
		uint8_t* destination,
		const uint8_t (&block)[64],
		const bc7enc_compress_block_params& bc7Params) noexcept
	{
		switch (format)
		{
		case DXGI_FORMAT_BC1_UNORM:
		case DXGI_FORMAT_BC1_UNORM_SRGB:
			rgbcx::encode_bc1(RGBCX_QUALITY_LEVEL, destination, block, true, false);
			break;
		case DXGI_FORMAT_BC3_UNORM:
		case DXGI_FORMAT_BC3_UNORM_SRGB:
			rgbcx::encode_bc3(RGBCX_QUALITY_LEVEL, destination, block);
			break;
		case DXGI_FORMAT_BC4_UNORM:
			rgbcx::encode_bc4(destination, block);
			break;
		case DXGI_FORMAT_BC5_UNORM:
			rgbcx::encode_bc5(destination, block);
			break;
		case DXGI_FORMAT_BC7_UNORM:
		case DXGI_FORMAT_BC7_UNORM_SRGB:
			bc7enc_compress_block(destination, block, &bc7Params);
			break;
		default:
			break;
		}
	}
}

void InitializeBcEncoder() noexcept
{
	rgbcx::init(rgbcx::bc1_approx_mode::cBC1Ideal);
	bc7enc_compress_block_init();
}

bool IsBcEncoderFormat(DXGI_FORMAT format) noexcept
{
	return GetBlockSize(format) != 0;
}

HRESULT CompressWithBcEncoder(
	const DirectX::Image& sourceImage,
	DXGI_FORMAT outputFormat,
	DirectX::ScratchImage& outputImage) noexcept
{
	if (!sourceImage.pixels)
		return E_POINTER;
	if (!sourceImage.width || !sourceImage.height || !IsBcEncoderFormat(outputFormat))
		return E_INVALIDARG;

	DirectX::ScratchImage convertedImage;
	const DirectX::Image* rgbaImage = &sourceImage;
	const DXGI_FORMAT rgbaFormat = DirectX::IsSRGB(outputFormat)
		? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
		: DXGI_FORMAT_R8G8B8A8_UNORM;
	if (sourceImage.format != rgbaFormat)
	{
		const HRESULT convertResult = DirectX::Convert(
			sourceImage,
			rgbaFormat,
			DirectX::TEX_FILTER_DEFAULT,
			DirectX::TEX_THRESHOLD_DEFAULT,
			convertedImage);
		if (FAILED(convertResult))
			return convertResult;

		rgbaImage = convertedImage.GetImage(0, 0, 0);
		if (!rgbaImage)
			return E_FAIL;
	}

	// rgbcx cannot encode BC1 punch-through alpha. Preserve that uncommon but
	// valid DXT1 case by asking the caller to use DirectXTex for this image.
	if ((outputFormat == DXGI_FORMAT_BC1_UNORM || outputFormat == DXGI_FORMAT_BC1_UNORM_SRGB)
		&& HasNonOpaqueAlpha(*rgbaImage))
	{
		return S_FALSE;
	}

	const HRESULT initializeResult = outputImage.Initialize2D(
		outputFormat, sourceImage.width, sourceImage.height, 1, 1);
	if (FAILED(initializeResult))
		return initializeResult;

	const DirectX::Image* destinationImage = outputImage.GetImage(0, 0, 0);
	if (!destinationImage)
		return E_FAIL;

	bc7enc_compress_block_params bc7Params;
	bc7enc_compress_block_params_init(&bc7Params);
	bc7Params.m_uber_level = BC7ENC_MAX_UBER_LEVEL;
	bc7Params.m_mode_partition_estimation_filterbank = BC7ENC_FALSE;

	const size_t blockSize = GetBlockSize(outputFormat);
	const size_t blockColumns = std::max<size_t>(1, (rgbaImage->width + 3) / 4);
	const ptrdiff_t blockRows = static_cast<ptrdiff_t>(std::max<size_t>(1, (rgbaImage->height + 3) / 4));

#pragma omp parallel for schedule(dynamic)
	for (ptrdiff_t blockY = 0; blockY < blockRows; ++blockY)
	{
		for (size_t blockX = 0; blockX < blockColumns; ++blockX)
		{
			uint8_t block[64];
			CopyBlock(*rgbaImage, blockX, static_cast<size_t>(blockY), block);
			uint8_t* destination = destinationImage->pixels
				+ static_cast<size_t>(blockY) * destinationImage->rowPitch
				+ blockX * blockSize;
			CompressBlock(outputFormat, destination, block, bc7Params);
		}
	}

	return S_OK;
}
