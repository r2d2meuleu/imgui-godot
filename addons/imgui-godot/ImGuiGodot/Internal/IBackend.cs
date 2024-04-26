#if GODOT_PC
using Godot;

namespace ImGuiGodot.Internal;

internal interface IBackend
{
    public bool Visible { get; set; }
    public float JoyAxisDeadZone { get; set; }
    public float Scale { get; set; }
    public void ResetFonts();
    public void AddFont(FontFile fontData, int fontSize, bool merge, ushort[]? glyphRanges);
    public void AddFontDefault();
    public void RebuildFontAtlas(float scale);
    public void Connect(Callable callable);
    public bool SubViewportWidget(SubViewport svp);
}
#endif
