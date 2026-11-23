using QRCoder;

namespace LolRemote.Companion;

public static class Qr
{
    public static byte[] Png(string text, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    public static List<string> ConsoleLines(string text, int quiet = 2)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var matrix = data.ModuleMatrix;
        var full = matrix.Count;
        var trim = Math.Max(0, 4 - quiet);
        var size = full - 2 * trim;

        bool Light(int row, int col)
        {
            if (row < 0 || row >= size || col < 0 || col >= size) return true;
            return !matrix[row + trim][col + trim];
        }

        var lines = new List<string>();
        for (var row = 0; row < size; row += 2)
        {
            var chars = new char[size];
            for (var col = 0; col < size; col++)
            {
                var top = Light(row, col);
                var bottom = row + 1 < size ? Light(row + 1, col) : true;
                chars[col] = top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ';
            }
            lines.Add(new string(chars));
        }
        return lines;
    }
}
