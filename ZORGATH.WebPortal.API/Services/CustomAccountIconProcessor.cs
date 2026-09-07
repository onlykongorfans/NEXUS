namespace ZORGATH.WebPortal.API.Services;

public sealed class CustomAccountIconProcessor
{
    public ProcessedAccountIcon Process(byte[] contents, int cropX = 0, int cropY = 0, int cropSize = 0)
    {
        if (contents.Length == 0 || contents.Length > CustomAccountIconConfiguration.MaximumUploadBytes)
            throw new ArgumentException("Upload a PNG file no larger than 2 MB.");

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];

        if (contents.Length < 33 || contents.AsSpan(0, 8).SequenceEqual(signature) is false
            || contents.AsSpan(12, 4).SequenceEqual("IHDR"u8) is false
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(8, 4)) != 13)
            throw new ArgumentException("The uploaded file must be a PNG image.");

        // Check Dimensions Before Allocating Pixel Buffers And Reject Animation Before Decoding
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(contents.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(contents.AsSpan(20, 4));

        if (width < 128 || height < 128 || width > CustomAccountIconConfiguration.MaximumSourceDimension || height > CustomAccountIconConfiguration.MaximumSourceDimension)
            throw new ArgumentException("The image must be between 128 and 2048 pixels on each side.");

        int offset = 8;
        bool hasEnd = false;

        while (offset <= contents.Length - 12)
        {
            uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(offset, 4));

            if (length > contents.Length - offset - 12)
                throw new ArgumentException("The PNG file is incomplete or invalid.");

            ReadOnlySpan<byte> type = contents.AsSpan(offset + 4, 4);

            if (type.SequenceEqual("acTL"u8))
                throw new ArgumentException("Animated PNG images are not supported.");

            if (type.SequenceEqual("IEND"u8))
            {
                hasEnd = true;
                break;
            }

            offset += (int) length + 12;
        }

        if (hasEnd is false)
            throw new ArgumentException("The PNG file is incomplete or invalid.");

        if (cropSize == 0)
        {
            cropSize = Math.Min(width, height);
            cropX = (width - cropSize) / 2;
            cropY = (height - cropSize) / 2;
        }

        if (cropSize < 128 || cropSize > Math.Min(width, height) || cropX < 0 || cropY < 0 || cropX > width - cropSize || cropY > height - cropSize)
            throw new ArgumentException("Choose a square crop inside the image, at least 128 pixels wide.");

        try
        {
            DecoderOptions decoderOptions = new () { MaxFrames = 1, SkipMetadata = true };
            using Image<Rgba32> image = Image.Load<Rgba32>(decoderOptions, contents);

            if (image.Width != width || image.Height != height)
                throw new ArgumentException("The PNG dimensions are invalid.");

            image.Mutate(operation => operation.Crop(new Rectangle(cropX, cropY, cropSize, cropSize))
                .Resize(CustomAccountIconConfiguration.ImageSize, CustomAccountIconConfiguration.ImageSize));

            using MemoryStream output = new ();
            image.Save(output, new PngEncoder { BitDepth = PngBitDepth.Bit8, ColorType = PngColorType.RgbWithAlpha, SkipMetadata = true });

            return new ProcessedAccountIcon(output.ToArray(), width, height, cropX, cropY, cropSize);
        }
        catch (UnknownImageFormatException)
        {
            throw new ArgumentException("The uploaded file must be a valid PNG image.");
        }
        catch (InvalidImageContentException)
        {
            throw new ArgumentException("The PNG file is damaged or unsupported.");
        }
    }
}

public sealed record ProcessedAccountIcon(byte[] PNG, int Width, int Height, int CropX, int CropY, int CropSize);
