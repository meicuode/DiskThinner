namespace DiskThinner.App.Controls;

/// <summary>
/// 自绘控件用的字体：从控件自己的 Font 派生粗体和小号字体。
/// PerMonitorV2 下 WinForms 会按控件所在显示器的 DPI 缩放控件的 Font，直接用 Theme 里的静态字体会和原生控件大小不一致，
/// 所以自绘时一律从 Control.Font 派生，并在字体或 DPI 变化后重建。
/// </summary>
internal sealed class DerivedFonts : IDisposable
{
    private Font? _source;

    public Font Regular { get; private set; } = Theme.Ui;
    public Font Bold { get; private set; } = Theme.UiBold;
    public Font Small { get; private set; } = Theme.Small;
    public Font SmallBold { get; private set; } = Theme.SmallBold;

    public void Update(Font source)
    {
        if (ReferenceEquals(source, _source))
        {
            return;
        }

        DisposeOwned();
        _source = source;
        Regular = source;
        Bold = new Font(source, FontStyle.Bold);
        var smallSize = source.SizeInPoints * (8.25f / 9f);
        Small = new Font(source.FontFamily, smallSize, FontStyle.Regular, GraphicsUnit.Point);
        SmallBold = new Font(source.FontFamily, smallSize, FontStyle.Bold, GraphicsUnit.Point);
    }

    private void DisposeOwned()
    {
        if (_source is null)
        {
            return;
        }

        Bold.Dispose();
        Small.Dispose();
        SmallBold.Dispose();
    }

    public void Dispose() => DisposeOwned();
}
