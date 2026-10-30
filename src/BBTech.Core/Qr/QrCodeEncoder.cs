using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BBTech.Core.Qr
{
    public enum EccLevel
    {
        L = 0, // ~7% error recovery
        M = 1, // ~15% error recovery
        Q = 2, // ~25% error recovery
        H = 3  // ~30% error recovery
    }

    /// <summary>
    /// Pure C# ISO/IEC 18004 standard QR Code generator with zero external dependencies.
    /// Supports Versions 1 to 40, all 4 ECC levels, Byte (8-bit) encoding, standard masking,
    /// and crisp uncompressed BMP image emission.
    /// </summary>
    public sealed class QrCodeEncoder
    {
        public int Version { get; }
        public EccLevel ErrorCorrection { get; }
        public int Size { get; }
        public bool[,] Modules { get; }
        public int DataBytes { get; }
        public int MaxCapacityBytes { get; }
        public string EncodingMode => "Byte (8-bit)";

        public QrCodeEncoder(int version, EccLevel ecc, bool[,] modules, int dataBytes = 0, int maxCap = 0)
        {
            Version = version;
            ErrorCorrection = ecc;
            Size = modules.GetLength(0);
            Modules = modules;
            DataBytes = dataBytes;
            MaxCapacityBytes = maxCap;
        }

        public static QrCodeEncoder EncodeText(string text, EccLevel ecc = EccLevel.M)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            byte[] data = Encoding.UTF8.GetBytes(text);
            return EncodeBinary(data, ecc);
        }

        public static QrCodeEncoder EncodeBinary(byte[] data, EccLevel ecc = EccLevel.M)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            // 1. Determine minimum version
            EccLevel effectiveEcc = ecc;
            int version = FindSmallestVersion(data.Length, effectiveEcc);

            // Adaptive ECC fallback: If requested ECC exceeds Version 40 capacity, fall back to lower ECC levels down to L
            if (version < 1 || version > 40)
            {
                for (int e = (int)ecc - 1; e >= 0; e--)
                {
                    var fallbackEcc = (EccLevel)e;
                    int v = FindSmallestVersion(data.Length, fallbackEcc);
                    if (v >= 1 && v <= 40)
                    {
                        version = v;
                        effectiveEcc = fallbackEcc;
                        break;
                    }
                }
            }

            if (version < 1 || version > 40)
            {
                int maxCapL = GetDataCapacity(40, EccLevel.L) - 3;
                throw new InvalidOperationException($"Payload size ({data.Length} bytes) exceeds QR code capacity (maximum {maxCapL} bytes supported in Version 40 Byte Mode at ECC Level L).");
            }

            int selectedCap = GetDataCapacity(version, effectiveEcc) - (version < 10 ? 2 : 3);

            // 2. Encode bitstream (Byte Mode: 0100 + char count indicator + data + terminator + padding)
            byte[] codewords = GenerateCodewords(data, version, effectiveEcc);

            // 3. Construct matrix with function patterns
            int size = 17 + 4 * version;
            bool[,] matrix = new bool[size, size];
            bool[,] isFunction = new bool[size, size];

            DrawFunctionPatterns(matrix, isFunction, version);

            // 4. Place data bits in zig-zag order
            PlaceDataBits(matrix, isFunction, codewords);

            // 5. Select best mask pattern (0..7) based on standard penalty rules
            int bestMask = 0;
            long lowestPenalty = long.MaxValue;
            bool[,] bestMatrix = (bool[,])matrix.Clone();

            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] testMatrix = (bool[,])matrix.Clone();
                ApplyMask(testMatrix, isFunction, mask);
                DrawFormatBits(testMatrix, effectiveEcc, mask);
                if (version >= 7) DrawVersionBits(testMatrix, version);

                long penalty = CalculatePenalty(testMatrix);
                if (penalty < lowestPenalty)
                {
                    lowestPenalty = penalty;
                    bestMask = mask;
                    bestMatrix = testMatrix;
                }
            }

            return new QrCodeEncoder(version, effectiveEcc, bestMatrix, data.Length, selectedCap);
        }

        #region BMP Generation

        /// <summary>
        /// Generates a valid uncompressed 24-bit RGB Windows BMP file as byte array.
        /// Fully portable, crisp integer pixels with white quiet-zone border.
        /// </summary>
        public byte[] ToBmp(int modulePixelSize = 6, int quietZoneModules = 4)
        {
            if (modulePixelSize < 1) modulePixelSize = 1;
            if (quietZoneModules < 0) quietZoneModules = 0;

            int totalModules = Size + 2 * quietZoneModules;
            int width = totalModules * modulePixelSize;
            int height = width;

            // Row size in bytes: each pixel is 3 bytes (BGR), padded to 4-byte boundary
            int rowBytes = (width * 3 + 3) & ~3;
            int imageSize = rowBytes * height;
            int fileSize = 54 + imageSize;

            byte[] bmp = new byte[fileSize];

            // BMP Header (14 bytes)
            bmp[0] = 0x42; // 'B'
            bmp[1] = 0x4D; // 'M'
            WriteInt32(bmp, 2, fileSize);
            WriteInt32(bmp, 6, 0); // Reserved
            WriteInt32(bmp, 10, 54); // Offset to pixel data

            // DIB Header (40 bytes - BITMAPINFOHEADER)
            WriteInt32(bmp, 14, 40); // Header size
            WriteInt32(bmp, 18, width);
            WriteInt32(bmp, 22, height); // Positive height
            WriteInt16(bmp, 26, 1);  // Color planes
            WriteInt16(bmp, 28, 24); // Bits per pixel (24-bit BGR)
            WriteInt32(bmp, 30, 0);  // BI_RGB uncompressed
            WriteInt32(bmp, 34, imageSize);
            WriteInt32(bmp, 38, 2835); // Horizontal resolution ~72 DPI
            WriteInt32(bmp, 42, 2835); // Vertical resolution ~72 DPI
            WriteInt32(bmp, 46, 0);    // Palette colors
            WriteInt32(bmp, 50, 0);    // Important colors

            // Fill pixels (standard BMP stores rows bottom to top)
            for (int y = 0; y < height; y++)
            {
                int modY = (height - 1 - y) / modulePixelSize - quietZoneModules;
                int rowOffset = 54 + y * rowBytes;

                for (int x = 0; x < width; x++)
                {
                    int modX = x / modulePixelSize - quietZoneModules;
                    bool isBlack = false;

                    if (modX >= 0 && modX < Size && modY >= 0 && modY < Size)
                    {
                        isBlack = Modules[modY, modX];
                    }

                    int pxOffset = rowOffset + x * 3;
                    byte color = (byte)(isBlack ? 0 : 255);
                    bmp[pxOffset] = color;     // Blue
                    bmp[pxOffset + 1] = color; // Green
                    bmp[pxOffset + 2] = color; // Red
                }
            }

            return bmp;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        #endregion

        #region Bitstream & Codeword Generation

        private static byte[] GenerateCodewords(byte[] data, int version, EccLevel ecc)
        {
            int totalDataBytes = GetDataCapacity(version, ecc);

            // Bitstream accumulator
            List<byte> bits = new List<byte>();

            // Mode indicator: 0100 for Byte Mode
            AppendBits(bits, 4, 4);

            // Character count indicator: 8 bits for v1-9, 16 bits for v10-40
            int charCountBits = version < 10 ? 8 : 16;
            AppendBits(bits, data.Length, charCountBits);

            // Data bytes
            foreach (byte b in data)
            {
                AppendBits(bits, b, 8);
            }

            // Terminator: up to four 0s
            int maxDataBits = totalDataBytes * 8;
            int termLen = Math.Min(4, maxDataBits - bits.Count);
            if (termLen > 0) AppendBits(bits, 0, termLen);

            // Pad to multiple of 8
            while ((bits.Count % 8) != 0) bits.Add(0);

            // Pad bytes (0xEC, 0x11 alternating)
            byte[] padBytes = { 0xEC, 0x11 };
            int padIdx = 0;
            while (bits.Count < maxDataBits)
            {
                AppendBits(bits, padBytes[padIdx % 2], 8);
                padIdx++;
            }

            // Pack bits into data codewords
            byte[] dataCodewords = new byte[totalDataBytes];
            for (int i = 0; i < totalDataBytes; i++)
            {
                int val = 0;
                for (int b = 0; b < 8; b++)
                {
                    val = (val << 1) | bits[i * 8 + b];
                }
                dataCodewords[i] = (byte)val;
            }

            // Error Correction Codeword Generation with Interleaving
            return InterleaveBlocks(dataCodewords, version, ecc);
        }

        private static void AppendBits(List<byte> list, int val, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                list.Add((byte)((val >> i) & 1));
            }
        }

        #endregion

        #region Function Patterns Placement

        private static void DrawFunctionPatterns(bool[,] matrix, bool[,] isFunction, int version)
        {
            int size = matrix.GetLength(0);

            // Finder patterns
            DrawFinder(matrix, isFunction, 0, 0);
            DrawFinder(matrix, isFunction, size - 7, 0);
            DrawFinder(matrix, isFunction, 0, size - 7);

            // Timing patterns
            for (int i = 8; i < size - 8; i++)
            {
                bool val = (i % 2) == 0;
                // Horizontal
                if (!isFunction[6, i])
                {
                    matrix[6, i] = val;
                    isFunction[6, i] = true;
                }
                // Vertical
                if (!isFunction[i, 6])
                {
                    matrix[i, 6] = val;
                    isFunction[i, 6] = true;
                }
            }

            // Alignment patterns
            int[] alignPos = GetAlignmentPatternPositions(version);
            for (int r = 0; r < alignPos.Length; r++)
            {
                for (int c = 0; c < alignPos.Length; c++)
                {
                    int ar = alignPos[r];
                    int ac = alignPos[c];
                    // Skip if overlapping finder patterns
                    if ((ar <= 8 && ac <= 8) || (ar <= 8 && ac >= size - 8) || (ar >= size - 8 && ac <= 8))
                        continue;

                    DrawAlignment(matrix, isFunction, ar, ac);
                }
            }

            // Dark module (always black)
            matrix[4 * version + 9, 8] = true;
            isFunction[4 * version + 9, 8] = true;

            // Reserve format info areas
            for (int i = 0; i < 9; i++)
            {
                isFunction[8, i] = true;
                isFunction[i, 8] = true;
            }
            for (int i = 0; i < 8; i++)
            {
                isFunction[8, size - 1 - i] = true;
                isFunction[size - 1 - i, 8] = true;
            }

            // Reserve version info areas (Version >= 7)
            if (version >= 7)
            {
                for (int r = 0; r < 6; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        isFunction[r, size - 11 + c] = true;
                        isFunction[size - 11 + c, r] = true;
                    }
                }
            }
        }

        private static void DrawFinder(bool[,] matrix, bool[,] isFunction, int top, int left)
        {
            int size = matrix.GetLength(0);
            for (int r = -1; r <= 7; r++)
            {
                for (int c = -1; c <= 7; c++)
                {
                    int mr = top + r;
                    int mc = left + c;
                    if (mr >= 0 && mr < size && mc >= 0 && mc < size)
                    {
                        bool isBlack = false;
                        if (r >= 0 && r <= 6 && c >= 0 && c <= 6)
                        {
                            isBlack = (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                        }
                        matrix[mr, mc] = isBlack;
                        isFunction[mr, mc] = true;
                    }
                }
            }
        }

        private static void DrawAlignment(bool[,] matrix, bool[,] isFunction, int centerR, int centerC)
        {
            for (int r = -2; r <= 2; r++)
            {
                for (int c = -2; c <= 2; c++)
                {
                    bool isBlack = Math.Abs(r) == 2 || Math.Abs(c) == 2 || (r == 0 && c == 0);
                    matrix[centerR + r, centerC + c] = isBlack;
                    isFunction[centerR + r, centerC + c] = true;
                }
            }
        }

        private static int GetFormatEccBits(EccLevel ecc)
        {
            switch (ecc)
            {
                case EccLevel.L: return 1; // 01b (ISO/IEC 18004 standard)
                case EccLevel.M: return 0; // 00b
                case EccLevel.Q: return 3; // 11b
                case EccLevel.H: return 2; // 10b
                default: return 0;
            }
        }

        private static bool GetBit(int val, int bitIndex) => ((val >> bitIndex) & 1) != 0;

        private static void DrawFormatBits(bool[,] matrix, EccLevel ecc, int mask)
        {
            int size = matrix.GetLength(0);
            int data = (GetFormatEccBits(ecc) << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
            {
                rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            }
            int fullFormat = ((data << 10) | (rem & 0x3FF)) ^ 0x5412;

            // Draw first copy (around top-left finder pattern)
            for (int i = 0; i <= 5; i++) matrix[i, 8] = GetBit(fullFormat, i);
            matrix[7, 8] = GetBit(fullFormat, 6);
            matrix[8, 8] = GetBit(fullFormat, 7);
            matrix[8, 7] = GetBit(fullFormat, 8);
            for (int i = 9; i < 15; i++) matrix[8, 14 - i] = GetBit(fullFormat, i);

            // Draw second copy (along bottom-left and top-right finder patterns)
            for (int i = 0; i < 8; i++) matrix[8, size - 1 - i] = GetBit(fullFormat, i);
            for (int i = 8; i < 15; i++) matrix[size - 15 + i, 8] = GetBit(fullFormat, i);

            // Dark module (always dark)
            matrix[size - 8, 8] = true;
        }

        private static void DrawVersionBits(bool[,] matrix, int version)
        {
            if (version < 7) return;
            int size = matrix.GetLength(0);
            int vData = version;
            int bch = vData;
            for (int i = 0; i < 12; i++)
            {
                bch = (bch << 1) ^ (((bch >> 11) & 1) * 0x1F25);
            }
            int fullVersion = (vData << 12) | bch;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((fullVersion >> i) & 1) != 0;
                int r = i / 3;
                int c = i % 3;
                // Bottom-left
                matrix[size - 11 + c, r] = bit;
                // Top-right
                matrix[r, size - 11 + c] = bit;
            }
        }

        #endregion

        #region Masking & Data Placement

        private static void PlaceDataBits(bool[,] matrix, bool[,] isFunction, byte[] codewords)
        {
            int size = matrix.GetLength(0);
            int bitIdx = 0;
            int totalBits = codewords.Length * 8;

            int right = size - 1;
            bool upward = true;

            while (right > 0)
            {
                if (right == 6) right--; // Skip vertical timing column

                for (int v = 0; v < size; v++)
                {
                    int row = upward ? (size - 1 - v) : v;

                    for (int col = right; col >= right - 1; col--)
                    {
                        if (!isFunction[row, col])
                        {
                            bool bit = false;
                            if (bitIdx < totalBits)
                            {
                                int byteIdx = bitIdx / 8;
                                int bitOffset = 7 - (bitIdx % 8);
                                bit = ((codewords[byteIdx] >> bitOffset) & 1) != 0;
                                bitIdx++;
                            }
                            matrix[row, col] = bit;
                        }
                    }
                }

                upward = !upward;
                right -= 2;
            }
        }

        private static void ApplyMask(bool[,] matrix, bool[,] isFunction, int mask)
        {
            int size = matrix.GetLength(0);
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (!isFunction[r, c])
                    {
                        bool invert = false;
                        switch (mask)
                        {
                            case 0: invert = ((r + c) % 2) == 0; break;
                            case 1: invert = (r % 2) == 0; break;
                            case 2: invert = (c % 3) == 0; break;
                            case 3: invert = ((r + c) % 3) == 0; break;
                            case 4: invert = ((r / 2 + c / 3) % 2) == 0; break;
                            case 5: invert = ((r * c) % 2 + (r * c) % 3) == 0; break;
                            case 6: invert = (((r * c) % 2 + (r * c) % 3) % 2) == 0; break;
                            case 7: invert = (((r + c) % 2 + (r * c) % 3) % 2) == 0; break;
                        }
                        if (invert) matrix[r, c] = !matrix[r, c];
                    }
                }
            }
        }

        private static long CalculatePenalty(bool[,] matrix)
        {
            int size = matrix.GetLength(0);
            long penalty = 0;

            // N1: 5+ consecutive same color modules
            for (int r = 0; r < size; r++)
            {
                int count = 1;
                for (int c = 1; c < size; c++)
                {
                    if (matrix[r, c] == matrix[r, c - 1])
                    {
                        count++;
                    }
                    else
                    {
                        if (count >= 5) penalty += 3 + (count - 5);
                        count = 1;
                    }
                }
                if (count >= 5) penalty += 3 + (count - 5);
            }

            for (int c = 0; c < size; c++)
            {
                int count = 1;
                for (int r = 1; r < size; r++)
                {
                    if (matrix[r, c] == matrix[r - 1, c])
                    {
                        count++;
                    }
                    else
                    {
                        if (count >= 5) penalty += 3 + (count - 5);
                        count = 1;
                    }
                }
                if (count >= 5) penalty += 3 + (count - 5);
            }

            // N2: 2x2 same color blocks
            for (int r = 0; r < size - 1; r++)
            {
                for (int c = 0; c < size - 1; c++)
                {
                    bool m = matrix[r, c];
                    if (matrix[r + 1, c] == m && matrix[r, c + 1] == m && matrix[r + 1, c + 1] == m)
                    {
                        penalty += 3;
                    }
                }
            }

            // N3: 1:1:3:1:1 pattern (finder-like patterns in data areas)
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c <= size - 11; c++)
                {
                    if (IsFinderLike(matrix[r, c], matrix[r, c + 1], matrix[r, c + 2], matrix[r, c + 3], matrix[r, c + 4],
                                     matrix[r, c + 5], matrix[r, c + 6], matrix[r, c + 7], matrix[r, c + 8], matrix[r, c + 9], matrix[r, c + 10]))
                    {
                        penalty += 40;
                    }
                }
            }

            for (int c = 0; c < size; c++)
            {
                for (int r = 0; r <= size - 11; r++)
                {
                    if (IsFinderLike(matrix[r, c], matrix[r + 1, c], matrix[r + 2, c], matrix[r + 3, c], matrix[r + 4, c],
                                     matrix[r + 5, c], matrix[r + 6, c], matrix[r + 7, c], matrix[r + 8, c], matrix[r + 9, c], matrix[r + 10, c]))
                    {
                        penalty += 40;
                    }
                }
            }

            // N4: Balance of dark and light modules
            int darkCount = 0;
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (matrix[r, c]) darkCount++;
                }
            }
            int totalModules = size * size;
            double percent = (double)darkCount / totalModules * 100.0;
            int prevMultipleOf5 = (int)(Math.Floor(percent / 5.0) * 5.0);
            int nextMultipleOf5 = prevMultipleOf5 + 5;
            int k = Math.Min(Math.Abs(prevMultipleOf5 - 50) / 5, Math.Abs(nextMultipleOf5 - 50) / 5);
            penalty += k * 10;

            return penalty;
        }

        private static bool IsFinderLike(bool b0, bool b1, bool b2, bool b3, bool b4, bool b5, bool b6, bool b7, bool b8, bool b9, bool b10)
        {
            // Pattern: 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0
            if (b0 && !b1 && b2 && b3 && b4 && !b5 && b6 && !b7 && !b8 && !b9 && !b10) return true;
            // Pattern: 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1
            if (!b0 && !b1 && !b2 && !b3 && b4 && !b5 && b6 && b7 && b8 && !b9 && b10) return true;
            return false;
        }

        #endregion

        #region Reed-Solomon Error Correction & Tables

        private static byte[] InterleaveBlocks(byte[] data, int version, EccLevel ecc)
        {
            int numBlocks = GetNumBlocks(version, ecc);
            int eccPerBlock = GetEccCodewordsPerBlock(version, ecc);

            int totalDataLen = data.Length;
            int shortBlockLen = totalDataLen / numBlocks;
            int numLongBlocks = totalDataLen % numBlocks;
            int numShortBlocks = numBlocks - numLongBlocks;

            byte[][] dataBlocks = new byte[numBlocks][];
            byte[][] eccBlocks = new byte[numBlocks][];

            int offset = 0;
            for (int b = 0; b < numBlocks; b++)
            {
                int blockLen = (b < numShortBlocks) ? shortBlockLen : (shortBlockLen + 1);
                byte[] block = new byte[blockLen];
                Array.Copy(data, offset, block, 0, blockLen);
                offset += blockLen;

                dataBlocks[b] = block;
                eccBlocks[b] = ComputeReedSolomon(block, eccPerBlock);
            }

            // Interleave data codewords
            List<byte> result = new List<byte>();
            int maxDataBlockLen = (numLongBlocks > 0) ? shortBlockLen + 1 : shortBlockLen;

            for (int i = 0; i < maxDataBlockLen; i++)
            {
                for (int b = 0; b < numBlocks; b++)
                {
                    if (i < dataBlocks[b].Length)
                    {
                        result.Add(dataBlocks[b][i]);
                    }
                }
            }

            // Interleave ECC codewords
            for (int i = 0; i < eccPerBlock; i++)
            {
                for (int b = 0; b < numBlocks; b++)
                {
                    result.Add(eccBlocks[b][i]);
                }
            }

            return result.ToArray();
        }

        private static byte[] ComputeReedSolomon(byte[] data, int eccCount)
        {
            byte[] generator = GetGeneratorPolynomial(eccCount);
            byte[] msg = new byte[data.Length + eccCount];
            Array.Copy(data, msg, data.Length);

            for (int i = 0; i < data.Length; i++)
            {
                byte lead = msg[i];
                if (lead != 0)
                {
                    for (int j = 0; j < generator.Length; j++)
                    {
                        msg[i + j] ^= GfMultiply(generator[j], lead);
                    }
                }
            }

            byte[] ecc = new byte[eccCount];
            Array.Copy(msg, data.Length, ecc, 0, eccCount);
            return ecc;
        }

        // Galois Field GF(256) tables with 0x11D primitive polynomial
        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrCodeEncoder()
        {
            int val = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)val;
                GfExp[i + 255] = (byte)val;
                GfLog[val] = (byte)i;
                val <<= 1;
                if ((val & 0x100) != 0) val ^= 0x11D;
            }
        }

        private static byte GfMultiply(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        private static byte[] GetGeneratorPolynomial(int degree)
        {
            byte[] poly = { 1 };
            for (int i = 0; i < degree; i++)
            {
                byte factor = GfExp[i];
                byte[] next = new byte[poly.Length + 1];
                for (int j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= GfMultiply(poly[j], factor);
                }
                poly = next;
            }
            return poly;
        }

        private static int FindSmallestVersion(int dataBytes, EccLevel ecc)
        {
            for (int v = 1; v <= 40; v++)
            {
                int cap = GetDataCapacity(v, ecc);
                int overhead = (v < 10) ? 2 : 3; // 4 bit mode + 8/16 bit count
                if (dataBytes + overhead <= cap)
                {
                    return v;
                }
            }
            return -1;
        }

        private static int GetDataCapacity(int version, EccLevel ecc)
        {
            int total = TotalCodewords[version - 1];
            int eccWords = GetEccCodewordsPerBlock(version, ecc) * GetNumBlocks(version, ecc);
            return total - eccWords;
        }

        private static int GetEccCodewordsPerBlock(int version, EccLevel ecc) => EccCodewordsPerBlockTable[(version - 1) * 4 + (int)ecc];
        private static int GetNumBlocks(int version, EccLevel ecc) => NumBlocksTable[(version - 1) * 4 + (int)ecc];

        private static readonly int[] TotalCodewords = {
            26, 44, 70, 100, 134, 172, 196, 242, 292, 346, 404, 466, 532, 581, 655, 733, 815, 901, 991, 1085,
            1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185, 2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706
        };

        private static readonly byte[] EccCodewordsPerBlockTable = {
             7, 10, 13, 17,  10, 16, 22, 28,  15, 26, 18, 22,  20, 18, 26, 16,  26, 24, 18, 22, // v1-5
            18, 16, 24, 28,  20, 18, 18, 26,  24, 22, 22, 26,  30, 22, 20, 24,  18, 26, 24, 28, // v6-10
            20, 30, 28, 24,  24, 22, 26, 28,  26, 22, 24, 22,  30, 24, 20, 24,  22, 24, 30, 24, // v11-15
            24, 28, 24, 30,  28, 28, 28, 28,  30, 26, 28, 28,  28, 26, 26, 26,  28, 26, 30, 28, // v16-20
            28, 26, 28, 30,  28, 28, 30, 24,  30, 28, 30, 30,  30, 28, 30, 30,  26, 28, 30, 30, // v21-25
            28, 28, 28, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30, // v26-30
            30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30, // v31-35
            30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30,  30, 28, 30, 30  // v36-40
        };

        private static readonly byte[] NumBlocksTable = {
             1,  1,  1,  1,   1,  1,  1,  1,   1,  1,  2,  2,   1,  2,  2,  4,   1,  2,  4,  4,
             2,  4,  4,  4,   2,  4,  6,  5,   2,  4,  6,  6,   2,  5,  8,  8,   4,  5,  8,  8,
             4,  5,  8, 11,   4,  8, 10, 11,   4,  9, 12, 16,   4,  9, 16, 16,   6, 10, 12, 18,
             6, 10, 17, 16,   6, 11, 16, 19,   6, 13, 18, 21,   7, 14, 21, 25,   8, 16, 20, 25,
             8, 17, 23, 25,   9, 17, 23, 34,   9, 18, 25, 30,  10, 20, 27, 32,  12, 21, 29, 35,
            12, 23, 34, 37,  12, 25, 34, 40,  13, 26, 35, 42,  14, 28, 38, 45,  15, 29, 40, 48,
            16, 31, 43, 51,  17, 33, 45, 54,  18, 35, 48, 57,  19, 37, 51, 60,  19, 38, 53, 63,
            20, 40, 56, 66,  21, 43, 59, 70,  22, 45, 62, 74,  24, 47, 65, 77,  25, 49, 68, 81
        };

        private static int[] GetAlignmentPatternPositions(int version)
        {
            if (version == 1) return new int[0];
            int num = version / 7 + 2;
            int step = (version == 32) ? 26 : (version * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
            int[] pos = new int[num];
            pos[0] = 6;
            for (int i = num - 1; i >= 1; i--)
            {
                pos[i] = 17 + 4 * version - 7 - (num - 1 - i) * step;
            }
            return pos;
        }

        #endregion
    }
}
