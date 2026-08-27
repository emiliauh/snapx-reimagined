
// SPDX-License-Identifier: GPL-3.0-or-later



using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SnapX.Core.Utils.Native;


namespace SnapX.Core.Utils;

public static class CaptureHelpers
{
    public static Rectangle GetScreenBounds()
    {
        return GetScreenWorkingArea();
    }

    public static Rectangle GetScreenWorkingArea() => Methods.GetWorkingArea().GetAwaiter().GetResult();

    public static Rectangle GetActiveScreenBounds()
    {
        return GetActiveScreenWorkingArea();
    }

    public static Rectangle GetActiveScreenWorkingArea() => Methods.GetActiveScreen().GetAwaiter().GetResult();

    public static Rectangle GetPrimaryScreenBounds() => Methods.GetPrimaryScreen().GetAwaiter().GetResult();

    public static Point ScreenToClient(Point p)
    {
        throw new NotImplementedException("ScreenToClient is not implemented");
    }

    public static Rectangle ScreenToClient(Rectangle r)
    {
        return new Rectangle(ScreenToClient(r.Location), r.Size);
    }

    public static Point ClientToScreen(Point p)
    {
        throw new NotImplementedException("ClientToScreen is not implemented");
    }

    public static Rectangle ClientToScreen(Rectangle r)
    {
        return new Rectangle(ClientToScreen(r.Location), r.Size);
    }

    public static Point GetCursorPosition() => Methods.GetCursorPosition();

    public static void SetCursorPosition(int x, int y)
    {
        throw new NotImplementedException("SetCursorPosition is not implemented");
    }

    public static void SetCursorPosition(Point position)
    {
        SetCursorPosition(position.X, position.Y);
    }

    public static Color GetPixelColor()
    {
        return GetPixelColor(GetCursorPosition());
    }

    public static Color GetPixelColor(int x, int y)
    {
        Point position = new(x, y);
        Rectangle screenBounds = Methods.GetScreenBounds(position);
        using Image? screenshot = Methods.CaptureScreen(position).GetAwaiter().GetResult();
        if (screenshot is null)
        {
            throw new InvalidOperationException("The screen capture backend returned no image.");
        }

        int pixelX = Math.Clamp(x - screenBounds.X, 0, screenshot.Width - 1);
        int pixelY = Math.Clamp(y - screenBounds.Y, 0, screenshot.Height - 1);
        using Image<Rgba64> rgba = screenshot.CloneAs<Rgba64>();
        return Color.FromPixel(rgba[pixelX, pixelY]);
    }

    public static Color GetPixelColor(Point position)
    {
        return GetPixelColor(position.X, position.Y);
    }

    public static bool CheckPixelColor(int x, int y, Color color)
    {
        Rgba32 actual = GetPixelColor(x, y).ToPixel<Rgba32>();
        Rgba32 expected = color.ToPixel<Rgba32>();
        return actual.R == expected.R && actual.G == expected.G && actual.B == expected.B;
    }

    public static bool CheckPixelColor(int x, int y, Color color, byte variation)
    {
        Rgba32 actual = GetPixelColor(x, y).ToPixel<Rgba32>();
        Rgba32 expected = color.ToPixel<Rgba32>();
        return Math.Abs(actual.R - expected.R) <= variation
            && Math.Abs(actual.G - expected.G) <= variation
            && Math.Abs(actual.B - expected.B) <= variation;
    }

    public static Rectangle CreateRectangle(int x, int y, int x2, int y2)
    {
        int width, height;

        if (x <= x2)
        {
            width = x2 - x + 1;
        }
        else
        {
            width = x - x2 + 1;
            x = x2;
        }

        if (y <= y2)
        {
            height = y2 - y + 1;
        }
        else
        {
            height = y - y2 + 1;
            y = y2;
        }

        return new Rectangle(x, y, width, height);
    }

    public static Rectangle CreateRectangle(Point pos, Point pos2)
    {
        return CreateRectangle(pos.X, pos.Y, pos2.X, pos2.Y);
    }

    public static RectangleF CreateRectangle(float x, float y, float x2, float y2)
    {
        float width, height;

        if (x <= x2)
        {
            width = x2 - x + 1;
        }
        else
        {
            width = x - x2 + 1;
            x = x2;
        }

        if (y <= y2)
        {
            height = y2 - y + 1;
        }
        else
        {
            height = y - y2 + 1;
            y = y2;
        }

        return new RectangleF(x, y, width, height);
    }

    public static RectangleF CreateRectangle(PointF pos, PointF pos2)
    {
        return CreateRectangle(pos.X, pos.Y, pos2.X, pos2.Y);
    }

    public static Point ProportionalPosition(Point pos, Point pos2)
    {
        var newPosition = Point.Empty;
        int min;

        if (pos.X < pos2.X)
        {
            if (pos.Y < pos2.Y)
            {
                min = MathHelpers.Min(pos2.X - pos.X, pos2.Y - pos.Y);
                newPosition.X = pos.X + min;
                newPosition.Y = pos.Y + min;
            }
            else
            {
                min = MathHelpers.Min(pos2.X - pos.X, pos.Y - pos2.Y);
                newPosition.X = pos.X + min;
                newPosition.Y = pos.Y - min;
            }
        }
        else
        {
            if (pos.Y > pos2.Y)
            {
                min = MathHelpers.Min(pos.X - pos2.X, pos.Y - pos2.Y);
                newPosition.X = pos.X - min;
                newPosition.Y = pos.Y - min;
            }
            else
            {
                min = MathHelpers.Min(pos.X - pos2.X, pos2.Y - pos.Y);
                newPosition.X = pos.X - min;
                newPosition.Y = pos.Y + min;
            }
        }

        return newPosition;
    }

    public static PointF SnapPositionToDegree(PointF pos, PointF pos2, float degree, float startDegree)
    {
        var angle = MathHelpers.LookAtRadian(pos, pos2);
        var startAngle = MathHelpers.DegreeToRadian(startDegree);
        var snapAngle = MathHelpers.DegreeToRadian(degree);
        var newAngle = ((float)System.Math.Round((angle + startAngle) / snapAngle) * snapAngle) - startAngle;
        var distance = MathHelpers.Distance(pos, pos2);

        var newVector = (PointF)MathHelpers.RadianToVector2(newAngle, distance);

        return new PointF(pos.X + newVector.X, pos.Y + newVector.Y);
    }

    public static PointF CalculateNewPosition(PointF posOnClick, PointF posCurrent, Size size)
    {
        if (posCurrent.X > posOnClick.X)
        {
            if (posCurrent.Y > posOnClick.Y)
            {
                return new PointF(posOnClick.X + size.Width - 1, posOnClick.Y + size.Height - 1);
            }
            else
            {
                return new PointF(posOnClick.X + size.Width - 1, posOnClick.Y - size.Height + 1);
            }
        }
        else
        {
            if (posCurrent.Y > posOnClick.Y)
            {
                return new PointF(posOnClick.X - size.Width + 1, posOnClick.Y + size.Height - 1);
            }
            else
            {
                return new PointF(posOnClick.X - size.Width + 1, posOnClick.Y - size.Height + 1);
            }
        }
    }

    public static RectangleF CalculateNewRectangle(PointF posOnClick, PointF posCurrent, Size size)
    {
        var newPosition = CalculateNewPosition(posOnClick, posCurrent, size);
        return CreateRectangle(posOnClick, newPosition);
    }

    public static Rectangle GetWindowRectangle(IntPtr handle) => Methods.GetWindowRectangle(handle);

    public static Rectangle GetActiveWindowRectangle()
    {
        // IntPtr handle = NativeMethods.GetForegroundWindow();
        // return GetWindowRectangle(handle);
        return Rectangle.Empty;
    }

    public static Rectangle GetActiveWindowClientRectangle()
    {
        // IntPtr handle = NativeMethods.GetForegroundWindow();
        // return NativeMethods.GetClientRect(handle);
        return Rectangle.Empty;
    }

    public static bool IsActiveWindowFullscreen()
    {
        // IntPtr handle = NativeMethods.GetForegroundWindow();
        //
        // if (handle.ToInt32() > 0)
        // {
        //     WindowInfo windowInfo = new WindowInfo(handle);
        //     string className = windowInfo.ClassName;
        //     string[] ignoreList = new string[] { "Progman", "WorkerW" };
        //
        //     if (ignoreList.All(ignore => !className.Equals(ignore, StringComparison.OrdinalIgnoreCase)))
        //     {
        //         Rectangle windowRectangle = windowInfo.Rectangle;
        //         Rectangle monitorRectangle = Screen.FromRectangle(windowRectangle).Bounds;
        //         return windowRectangle.Contains(monitorRectangle);
        //     }
        // }

        return false;
    }

    public static Rectangle EvenRectangleSize(Rectangle rect)
    {
        rect.Width -= rect.Width & 1;
        rect.Height -= rect.Height & 1;
        return rect;
    }
}
