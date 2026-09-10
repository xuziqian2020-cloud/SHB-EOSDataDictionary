using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SHB.EosDataDictionary.IconGenerator
{
    /// <summary>XMZADD 20260828 绘制数据库与关联节点品牌图标并生成 PNG 和 ICO 资产。</summary>
    internal static class Program
    {
        /// <summary>XMZADD 20260828 根据输出目录生成可供 WPF 和 Windows EXE 使用的图标文件。</summary>
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine("请提供图标输出目录。");
                return 1;
            }

            string outputDirectory = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(outputDirectory);
            string pngPath = Path.Combine(outputDirectory, "EosDataDictionary.png");
            string icoPath = Path.Combine(outputDirectory, "EosDataDictionary.ico");

            using (Bitmap bitmap = DrawIcon())
            using (MemoryStream pngStream = new MemoryStream())
            {
                bitmap.Save(pngStream, ImageFormat.Png);
                byte[] pngBytes = pngStream.ToArray();
                File.WriteAllBytes(pngPath, pngBytes);
                WriteIco(icoPath, pngBytes);
            }

            Console.WriteLine(pngPath);
            Console.WriteLine(icoPath);
            return 0;
        }

        /// <summary>XMZADD 20260828 以蓝青双色绘制数据库圆柱和两个关联节点的品牌图形。</summary>
        private static Bitmap DrawIcon()
        {
            Bitmap bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);

                using (GraphicsPath outerPath = CreateRoundedRectangle(new RectangleF(12, 12, 232, 232), 38))
                using (LinearGradientBrush outerBrush = new LinearGradientBrush(new PointF(18, 18), new PointF(238, 238), Color.FromArgb(255, 8, 49, 80), Color.FromArgb(255, 5, 25, 44)))
                using (Pen outerPen = new Pen(Color.FromArgb(255, 27, 137, 201), 5))
                {
                    graphics.FillPath(outerBrush, outerPath);
                    graphics.DrawPath(outerPen, outerPath);
                }

                using (GraphicsPath databaseBody = new GraphicsPath())
                using (LinearGradientBrush databaseBrush = new LinearGradientBrush(new PointF(57, 78), new PointF(143, 170), Color.FromArgb(255, 21, 143, 205), Color.FromArgb(255, 8, 79, 122)))
                using (Pen cyanPen = new Pen(Color.FromArgb(255, 114, 215, 255), 5))
                {
                    databaseBody.AddLine(57, 78, 57, 156);
                    databaseBody.AddBezier(57, 156, 57, 176, 143, 176, 143, 156);
                    databaseBody.AddLine(143, 156, 143, 78);
                    databaseBody.CloseFigure();
                    graphics.FillPath(databaseBrush, databaseBody);
                    graphics.DrawPath(cyanPen, databaseBody);
                    graphics.FillEllipse(databaseBrush, 57, 60, 86, 36);
                    graphics.DrawEllipse(cyanPen, 57, 60, 86, 36);
                    graphics.DrawArc(cyanPen, 57, 94, 86, 34, 0, 180);
                    graphics.DrawArc(cyanPen, 57, 126, 86, 34, 0, 180);
                }

                using (Pen linkPen = new Pen(Color.FromArgb(255, 39, 215, 196), 7))
                using (SolidBrush nodeBrush = new SolidBrush(Color.FromArgb(255, 39, 215, 196)))
                using (SolidBrush nodeInnerBrush = new SolidBrush(Color.FromArgb(255, 7, 49, 75)))
                using (Pen highlightPen = new Pen(Color.FromArgb(210, 255, 255, 255), 4))
                {
                    linkPen.StartCap = LineCap.Round;
                    linkPen.EndCap = LineCap.Round;
                    graphics.DrawLine(linkPen, 142, 92, 180, 76);
                    graphics.DrawLine(linkPen, 143, 142, 188, 177);
                    graphics.FillEllipse(nodeBrush, 171, 57, 38, 38);
                    graphics.FillEllipse(nodeInnerBrush, 180, 66, 20, 20);
                    graphics.FillEllipse(nodeBrush, 181, 158, 42, 42);
                    graphics.FillEllipse(nodeInnerBrush, 191, 168, 22, 22);
                    graphics.DrawLine(highlightPen, 82, 77, 118, 77);
                }
            }
            return bitmap;
        }

        /// <summary>XMZADD 20260828 创建统一圆角矩形路径，使图标外框在各尺寸下保持一致。</summary>
        private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2;
            RectangleF arc = new RectangleF(rectangle.X, rectangle.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>XMZADD 20260828 将 256px PNG 按 Windows ICO 规范封装为可执行文件图标。</summary>
        private static void WriteIco(string icoPath, byte[] pngBytes)
        {
            using (FileStream file = new FileStream(icoPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)pngBytes.Length);
                writer.Write((uint)22);
                writer.Write(pngBytes);
            }
        }
    }
}
