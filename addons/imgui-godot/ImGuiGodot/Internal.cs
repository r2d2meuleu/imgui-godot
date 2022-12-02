using Godot;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using CursorShape = Godot.DisplayServer.CursorShape;

namespace ImGuiGodot;

internal interface IRenderer
{
    public string Name { get; }
    public void Init(ImGuiIOPtr io);
    public void InitViewport(Viewport vp);
    public void CloseViewport(Viewport vp);
    public void RenderDrawData(Viewport vp, ImDrawDataPtr drawData);
    public void OnHide();
    public void Shutdown();
}

internal static class Internal
{
    internal static SubViewport CurrentSubViewport { get; set; }
    internal static System.Numerics.Vector2 CurrentSubViewportPos { get; set; }

    private static Texture2D _fontTexture;
    private static Vector2 _mouseWheel = Vector2.Zero;
    private static ImGuiMouseCursor _currentCursor = ImGuiMouseCursor.None;
    private static readonly IntPtr _backendName = Marshal.StringToCoTaskMemAnsi("imgui_impl_godot4_net");
    private static IntPtr _rendererName = IntPtr.Zero;
    private static IntPtr _iniFilenameBuffer = IntPtr.Zero;
    internal static IRenderer Renderer { get; private set; }

    private class FontParams
    {
        public FontFile Font { get; init; }
        public int FontSize { get; init; }
        public bool Merge { get; init; }
    }
    private static readonly List<FontParams> _fontConfiguration = new();

    internal static readonly Func<ulong, RID> ConstructRID;

    static Internal()
    {
        ConstructorInfo cinfo = typeof(RID).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(ulong) });
        if (cinfo is null)
        {
            throw new PlatformNotSupportedException("failed to get RID constructor");
        }

        DynamicMethod dm = new("ConstructRID", typeof(RID), new[] { typeof(ulong) });
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, cinfo);
        il.Emit(OpCodes.Ret);
        ConstructRID = dm.CreateDelegate<Func<ulong, RID>>();
    }

    public static void AddFont(FontFile fontData, int fontSize, bool merge)
    {
        _fontConfiguration.Add(new FontParams { Font = fontData, FontSize = fontSize, Merge = merge });
    }

    private static unsafe void AddFontToAtlas(FontFile fontData, int fontSize, bool merge)
    {
        ImFontConfig* fc = ImGuiNative.ImFontConfig_ImFontConfig();
        if (merge)
        {
            fc->MergeMode = 1;
        }

        if (fontData == null)
        {
            // default font
            var fcptr = new ImFontConfigPtr(fc)
            {
                SizePixels = fontSize,
                OversampleH = 1,
                OversampleV = 1,
                PixelSnapH = true
            };
            ImGui.GetIO().Fonts.AddFontDefault(fc);
        }
        else
        {
            ImVector ranges = GetRanges(fontData);
            string name = $"{System.IO.Path.GetFileName(fontData.ResourcePath)}, {fontSize}px";
            for (int i = 0; i < name.Length && i < 40; ++i)
            {
                fc->Name[i] = Convert.ToByte(name[i]);
            }

            int len = fontData.Data.Length;
            // let ImGui manage this memory
            IntPtr p = ImGui.MemAlloc((uint)len);
            Marshal.Copy(fontData.Data, 0, p, len);
            ImGui.GetIO().Fonts.AddFontFromMemoryTTF(p, len, fontSize, fc, ranges.Data);
        }

        if (merge)
        {
            ImGui.GetIO().Fonts.Build();
        }
        ImGuiNative.ImFontConfig_destroy(fc);
    }

    private static unsafe ImVector GetRanges(Font font)
    {
        var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
        builder.AddText(font.GetSupportedChars());
        builder.BuildRanges(out ImVector vec);
        builder.Destroy();
        return vec;
    }

    private static unsafe void ResetStyle()
    {
        ImGuiStylePtr defaultStyle = new(ImGuiNative.ImGuiStyle_ImGuiStyle());
        ImGuiStylePtr style = ImGui.GetStyle();

        style.WindowPadding = defaultStyle.WindowPadding;
        style.WindowRounding = defaultStyle.WindowRounding;
        style.WindowMinSize = defaultStyle.WindowMinSize;
        style.ChildRounding = defaultStyle.ChildRounding;
        style.PopupRounding = defaultStyle.PopupRounding;
        style.FramePadding = defaultStyle.FramePadding;
        style.FrameRounding = defaultStyle.FrameRounding;
        style.ItemSpacing = defaultStyle.ItemSpacing;
        style.ItemInnerSpacing = defaultStyle.ItemInnerSpacing;
        style.CellPadding = defaultStyle.CellPadding;
        style.TouchExtraPadding = defaultStyle.TouchExtraPadding;
        style.IndentSpacing = defaultStyle.IndentSpacing;
        style.ColumnsMinSpacing = defaultStyle.ColumnsMinSpacing;
        style.ScrollbarSize = defaultStyle.ScrollbarSize;
        style.ScrollbarRounding = defaultStyle.ScrollbarRounding;
        style.GrabMinSize = defaultStyle.GrabMinSize;
        style.GrabRounding = defaultStyle.GrabRounding;
        style.LogSliderDeadzone = defaultStyle.LogSliderDeadzone;
        style.TabRounding = defaultStyle.TabRounding;
        style.TabMinWidthForCloseButton = defaultStyle.TabMinWidthForCloseButton;
        style.DisplayWindowPadding = defaultStyle.DisplayWindowPadding;
        style.DisplaySafeAreaPadding = defaultStyle.DisplaySafeAreaPadding;
        style.MouseCursorScale = defaultStyle.MouseCursorScale;

        defaultStyle.Destroy();
    }

    public static unsafe void RebuildFontAtlas(float scale)
    {
        var io = ImGui.GetIO();
        int fontIndex = -1;
        if (io.NativePtr->FontDefault != null)
        {
            for (int i = 0; i < io.Fonts.Fonts.Size; ++i)
            {
                if (io.Fonts.Fonts[i].NativePtr == io.FontDefault.NativePtr)
                {
                    fontIndex = i;
                    break;
                }
            }
            io.NativePtr->FontDefault = null;
        }
        io.Fonts.Clear();

        foreach (var fontParams in _fontConfiguration)
        {
            AddFontToAtlas(fontParams.Font, (int)(fontParams.FontSize * scale), fontParams.Merge);
        }

        io.Fonts.GetTexDataAsRGBA32(out byte* pixelData, out int width, out int height, out int bytesPerPixel);

        byte[] pixels = new byte[width * height * bytesPerPixel];
        Marshal.Copy((IntPtr)pixelData, pixels, 0, pixels.Length);

        var img = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);

        var imgtex = ImageTexture.CreateFromImage(img);
        _fontTexture = imgtex;
        io.Fonts.SetTexID((IntPtr)_fontTexture.GetRid().Id);
        io.Fonts.ClearTexData();

        if (fontIndex != -1 && fontIndex < io.Fonts.Fonts.Size)
        {
            io.NativePtr->FontDefault = io.Fonts.Fonts[fontIndex].NativePtr;
        }

        ResetStyle();
        ImGui.GetStyle().ScaleAllSizes(scale);
    }

    public static void Init(IRenderer renderer)
    {
        Renderer = renderer;
        _fontConfiguration.Clear();

        if (ImGui.GetCurrentContext() != IntPtr.Zero)
        {
            ImGui.DestroyContext();
        }

        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        var io = ImGui.GetIO();

        io.BackendFlags = 0;
        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;

        if (_rendererName == IntPtr.Zero)
        {
            _rendererName = Marshal.StringToCoTaskMemAnsi(Renderer.Name);
        }

        unsafe
        {
            io.NativePtr->BackendPlatformName = (byte*)_backendName;
            io.NativePtr->BackendRendererName = (byte*)_rendererName;
        }

        Renderer.Init(io);
        InternalViewports.Init();
    }

    public static void ResetFonts()
    {
        var io = ImGui.GetIO();
        io.Fonts.Clear();
        unsafe { io.NativePtr->FontDefault = null; }
        _fontConfiguration.Clear();
    }

    public static unsafe void SetIniFilename(ImGuiIOPtr io, string fileName)
    {
        io.NativePtr->IniFilename = null;

        if (_iniFilenameBuffer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_iniFilenameBuffer);
            _iniFilenameBuffer = IntPtr.Zero;
        }

        if (fileName?.Length > 0)
        {
            fileName = ProjectSettings.GlobalizePath(fileName);
            _iniFilenameBuffer = Marshal.StringToCoTaskMemUTF8(fileName);
            io.NativePtr->IniFilename = (byte*)_iniFilenameBuffer;
        }
    }

    public static void Update(double delta, Viewport vp)
    {
        var io = ImGui.GetIO();
        var vpSize = vp.GetVisibleRect().Size;
        io.DisplaySize = new(vpSize.x, vpSize.y);
        io.DeltaTime = (float)delta;

        if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable))
        {
            var mousePos = DisplayServer.MouseGetPosition();
            io.AddMousePosEvent(mousePos.x, mousePos.y);

            if (io.WantSetMousePos)
            {
                // TODO: get current focused window
            }
        }
        else
        {
            if (io.WantSetMousePos)
                Input.WarpMouse(new(io.MousePos.X, io.MousePos.Y));
        }

        // scrolling works better if we allow no more than one event per frame
        if (_mouseWheel != Vector2.Zero)
        {
            io.AddMouseWheelEvent(_mouseWheel.x, _mouseWheel.y);
            _mouseWheel = Vector2.Zero;
        }

        if (io.WantCaptureMouse && !io.ConfigFlags.HasFlag(ImGuiConfigFlags.NoMouseCursorChange))
        {
            var newCursor = ImGui.GetMouseCursor();
            if (newCursor != _currentCursor)
            {
                DisplayServer.CursorSetShape(ConvertCursorShape(newCursor));
                _currentCursor = newCursor;
            }
        }
        else
        {
            _currentCursor = ImGuiMouseCursor.None;
        }

        CurrentSubViewport = null;
        ImGui.NewFrame();
    }

    public static bool ProcessInput(InputEvent evt, Window window)
    {
        var io = ImGui.GetIO();
        bool viewportsEnable = io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable);

        var windowPos = Vector2i.Zero;
        if (viewportsEnable)
            windowPos = window.Position;

        if (CurrentSubViewport != null)
        {
            var vpEvent = evt.Duplicate() as InputEvent;
            if (vpEvent is InputEventMouse mouseEvent)
            {
                mouseEvent.Position = new Vector2(windowPos.x + mouseEvent.GlobalPosition.x - CurrentSubViewportPos.X,
                    windowPos.y + mouseEvent.GlobalPosition.y - CurrentSubViewportPos.Y)
                    .Clamp(Vector2.Zero, CurrentSubViewport.Size);
            }
            CurrentSubViewport.PushInput(vpEvent, true);
            if (!CurrentSubViewport.IsInputHandled())
            {
                CurrentSubViewport.PushUnhandledInput(vpEvent, true);
            }
        }

        bool consumed = false;

        if (evt is InputEventMouseMotion mm)
        {
            if (!viewportsEnable)
                io.AddMousePosEvent(mm.GlobalPosition.x, mm.GlobalPosition.y);
            consumed = io.WantCaptureMouse;
        }
        else if (evt is InputEventMouseButton mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left:
                    io.AddMouseButtonEvent((int)ImGuiMouseButton.Left, mb.Pressed);
                    break;
                case MouseButton.Right:
                    io.AddMouseButtonEvent((int)ImGuiMouseButton.Right, mb.Pressed);
                    break;
                case MouseButton.Middle:
                    io.AddMouseButtonEvent((int)ImGuiMouseButton.Middle, mb.Pressed);
                    break;
                case MouseButton.Xbutton1:
                    io.AddMouseButtonEvent((int)ImGuiMouseButton.Middle + 1, mb.Pressed);
                    break;
                case MouseButton.Xbutton2:
                    io.AddMouseButtonEvent((int)ImGuiMouseButton.Middle + 2, mb.Pressed);
                    break;
                case MouseButton.WheelUp:
                    _mouseWheel.y = mb.Factor;
                    break;
                case MouseButton.WheelDown:
                    _mouseWheel.y = -mb.Factor;
                    break;
                case MouseButton.WheelLeft:
                    _mouseWheel.x = -mb.Factor;
                    break;
                case MouseButton.WheelRight:
                    _mouseWheel.x = mb.Factor;
                    break;
            };
            consumed = io.WantCaptureMouse;
        }
        else if (evt is InputEventKey k)
        {
            UpdateKeyMods(io);
            ImGuiKey igk = ConvertKey(k.Keycode);
            if (igk != ImGuiKey.None)
            {
                io.AddKeyEvent(igk, k.Pressed);

                if (k.Pressed && k.Unicode != 0 && io.WantTextInput)
                {
                    io.AddInputCharacter((uint)k.Unicode);
                }
            }
            consumed = io.WantCaptureKeyboard || io.WantTextInput;
        }
        else if (evt is InputEventPanGesture pg)
        {
            _mouseWheel = new(-pg.Delta.x, -pg.Delta.y);
            consumed = io.WantCaptureMouse;
        }
        else if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.NavEnableGamepad))
        {
            if (evt is InputEventJoypadButton jb)
            {
                ImGuiKey igk = ConvertJoyButton(jb.ButtonIndex);
                if (igk != ImGuiKey.None)
                {
                    io.AddKeyEvent(igk, jb.Pressed);
                    consumed = true;
                }
            }
            else if (evt is InputEventJoypadMotion jm)
            {
                bool pressed = true;
                float v = jm.AxisValue;
                if (Math.Abs(v) < ImGuiGD.JoyAxisDeadZone)
                {
                    v = 0f;
                    pressed = false;
                }
                switch (jm.Axis)
                {
                    case JoyAxis.LeftX:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, pressed, v);
                        break;
                    case JoyAxis.LeftY:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, pressed, v);
                        break;
                    case JoyAxis.RightX:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickRight, pressed, v);
                        break;
                    case JoyAxis.RightY:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickDown, pressed, v);
                        break;
                    case JoyAxis.TriggerLeft:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadL2, pressed, v);
                        break;
                    case JoyAxis.TriggerRight:
                        io.AddKeyAnalogEvent(ImGuiKey.GamepadR2, pressed, v);
                        break;
                };
                consumed = true;
            }
        }

        return consumed;
    }

    private static void UpdateKeyMods(ImGuiIOPtr io)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, Input.IsKeyPressed(Key.Ctrl));
        io.AddKeyEvent(ImGuiKey.ModShift, Input.IsKeyPressed(Key.Shift));
        io.AddKeyEvent(ImGuiKey.ModAlt, Input.IsKeyPressed(Key.Alt));
        io.AddKeyEvent(ImGuiKey.ModSuper, Input.IsKeyPressed(Key.SuperL));
    }

    public static void ProcessNotification(long what)
    {
        switch (what)
        {
            case MainLoop.NotificationApplicationFocusIn:
                ImGui.GetIO().AddFocusEvent(true);
                break;
            case MainLoop.NotificationApplicationFocusOut:
                ImGui.GetIO().AddFocusEvent(false);
                break;
        };
    }

    public static void AddLayerSubViewport(Node parent, out SubViewportContainer subViewportContainer, out SubViewport subViewport)
    {
        subViewportContainer = new SubViewportContainer
        {
            Name = "ImGuiLayer_SubViewportContainer",
            AnchorsPreset = 15,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Stretch = true
        };

        subViewport = new SubViewport
        {
            Name = "ImGuiLayer_SubViewport",
            TransparentBg = true,
            HandleInputLocally = false,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };

        subViewportContainer.AddChild(subViewport);
        parent.AddChild(subViewportContainer);
    }

    public static void Render(Viewport vp)
    {
        ImGui.Render();
        Renderer.RenderDrawData(vp, ImGui.GetDrawData());

        var io = ImGui.GetIO();
        if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable))
        {
            ImGui.UpdatePlatformWindows();
            InternalViewports.RenderViewports();
        }
    }

    private static CursorShape ConvertCursorShape(ImGuiMouseCursor cur) => cur switch
    {
        ImGuiMouseCursor.Arrow => CursorShape.Arrow,
        ImGuiMouseCursor.TextInput => CursorShape.Ibeam,
        ImGuiMouseCursor.ResizeAll => CursorShape.Move,
        ImGuiMouseCursor.ResizeNS => CursorShape.Vsize,
        ImGuiMouseCursor.ResizeEW => CursorShape.Hsize,
        ImGuiMouseCursor.ResizeNESW => CursorShape.Bdiagsize,
        ImGuiMouseCursor.ResizeNWSE => CursorShape.Fdiagsize,
        ImGuiMouseCursor.Hand => CursorShape.PointingHand,
        ImGuiMouseCursor.NotAllowed => CursorShape.Forbidden,
        _ => CursorShape.Arrow,
    };

    public static ImGuiKey ConvertJoyButton(JoyButton btn) => btn switch
    {
        JoyButton.Start => ImGuiKey.GamepadStart,
        JoyButton.Back => ImGuiKey.GamepadBack,
        JoyButton.Y => ImGuiKey.GamepadFaceUp,
        JoyButton.A => ImGuiGD.JoyButtonSwapAB ? ImGuiKey.GamepadFaceRight : ImGuiKey.GamepadFaceDown,
        JoyButton.X => ImGuiKey.GamepadFaceLeft,
        JoyButton.B => ImGuiGD.JoyButtonSwapAB ? ImGuiKey.GamepadFaceDown : ImGuiKey.GamepadFaceRight,
        JoyButton.DpadUp => ImGuiKey.GamepadDpadUp,
        JoyButton.DpadDown => ImGuiKey.GamepadDpadDown,
        JoyButton.DpadLeft => ImGuiKey.GamepadDpadLeft,
        JoyButton.DpadRight => ImGuiKey.GamepadDpadRight,
        JoyButton.LeftShoulder => ImGuiKey.GamepadL1,
        JoyButton.RightShoulder => ImGuiKey.GamepadR1,
        JoyButton.LeftStick => ImGuiKey.GamepadL3,
        JoyButton.RightStick => ImGuiKey.GamepadR3,
        _ => ImGuiKey.None
    };

    public static ImGuiKey ConvertKey(Key k) => k switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.Pageup => ImGuiKey.PageUp,
        Key.Pagedown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.Ctrl => ImGuiKey.LeftCtrl,
        Key.Shift => ImGuiKey.LeftShift,
        Key.Alt => ImGuiKey.LeftAlt,
        Key.SuperL => ImGuiKey.LeftSuper,
        Key.SuperR => ImGuiKey.RightSuper,
        Key.Menu => ImGuiKey.Menu,
        Key.Key0 => ImGuiKey._0,
        Key.Key1 => ImGuiKey._1,
        Key.Key2 => ImGuiKey._2,
        Key.Key3 => ImGuiKey._3,
        Key.Key4 => ImGuiKey._4,
        Key.Key5 => ImGuiKey._5,
        Key.Key6 => ImGuiKey._6,
        Key.Key7 => ImGuiKey._7,
        Key.Key8 => ImGuiKey._8,
        Key.Key9 => ImGuiKey._9,
        Key.Apostrophe => ImGuiKey.Apostrophe,
        Key.Comma => ImGuiKey.Comma,
        Key.Minus => ImGuiKey.Minus,
        Key.Period => ImGuiKey.Period,
        Key.Slash => ImGuiKey.Slash,
        Key.Semicolon => ImGuiKey.Semicolon,
        Key.Equal => ImGuiKey.Equal,
        Key.Bracketleft => ImGuiKey.LeftBracket,
        Key.Backslash => ImGuiKey.Backslash,
        Key.Bracketright => ImGuiKey.RightBracket,
        Key.Quoteleft => ImGuiKey.GraveAccent,
        Key.Capslock => ImGuiKey.CapsLock,
        Key.Scrolllock => ImGuiKey.ScrollLock,
        Key.Numlock => ImGuiKey.NumLock,
        Key.Print => ImGuiKey.PrintScreen,
        Key.Pause => ImGuiKey.Pause,
        Key.Kp0 => ImGuiKey.Keypad0,
        Key.Kp1 => ImGuiKey.Keypad1,
        Key.Kp2 => ImGuiKey.Keypad2,
        Key.Kp3 => ImGuiKey.Keypad3,
        Key.Kp4 => ImGuiKey.Keypad4,
        Key.Kp5 => ImGuiKey.Keypad5,
        Key.Kp6 => ImGuiKey.Keypad6,
        Key.Kp7 => ImGuiKey.Keypad7,
        Key.Kp8 => ImGuiKey.Keypad8,
        Key.Kp9 => ImGuiKey.Keypad9,
        Key.KpPeriod => ImGuiKey.KeypadDecimal,
        Key.KpDivide => ImGuiKey.KeypadDivide,
        Key.KpMultiply => ImGuiKey.KeypadMultiply,
        Key.KpSubtract => ImGuiKey.KeypadSubtract,
        Key.KpAdd => ImGuiKey.KeypadAdd,
        Key.KpEnter => ImGuiKey.KeypadEnter,
        Key.A => ImGuiKey.A,
        Key.B => ImGuiKey.B,
        Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E,
        Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G,
        Key.H => ImGuiKey.H,
        Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J,
        Key.K => ImGuiKey.K,
        Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M,
        Key.N => ImGuiKey.N,
        Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P,
        Key.Q => ImGuiKey.Q,
        Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S,
        Key.T => ImGuiKey.T,
        Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V,
        Key.W => ImGuiKey.W,
        Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y,
        Key.Z => ImGuiKey.Z,
        Key.F1 => ImGuiKey.F1,
        Key.F2 => ImGuiKey.F2,
        Key.F3 => ImGuiKey.F3,
        Key.F4 => ImGuiKey.F4,
        Key.F5 => ImGuiKey.F5,
        Key.F6 => ImGuiKey.F6,
        Key.F7 => ImGuiKey.F7,
        Key.F8 => ImGuiKey.F8,
        Key.F9 => ImGuiKey.F9,
        Key.F10 => ImGuiKey.F10,
        Key.F11 => ImGuiKey.F11,
        Key.F12 => ImGuiKey.F12,
        _ => ImGuiKey.None
    };
}
