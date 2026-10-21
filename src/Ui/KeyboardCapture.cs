using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TvRemote.Interop;

namespace TvRemote.Ui
{
    // Modo teclado: captura teclas no Windows inteiro (gancho de teclado de baixo nível) e entrega como teclas da TV.
    // Setas, Enter, Backspace e as teclas de volume e mídia; Esc e play/pausa ficam para quem usa decidir.
    internal sealed class KeyboardCapture
    {
        internal const string Escape = "ESC";
        internal const string PlayPause = "PLAYPAUSE";

        // Tecla virtual do Windows → tecla da TV, sem o prefixo "KEY_".
        private static readonly Dictionary<int, string> s_keys = new Dictionary<int, string>
        {
            { 0x25, "LEFT" },
            { 0x26, "UP" },
            { 0x27, "RIGHT" },
            { 0x28, "DOWN" },
            { 0x0D, "ENTER" },
            { 0x08, "RETURN" },     // Backspace
            { 0x1B, Escape },
            { 0xAD, "MUTE" },       // Teclas de volume e mídia do teclado.
            { 0xAE, "VOLDOWN" },
            { 0xAF, "VOLUP" },
            { 0xB0, "FF" },         // Próxima faixa.
            { 0xB1, "REWIND" },     // Faixa anterior.
            { 0xB2, "STOP" },
            { 0xB3, PlayPause },
        };

        // Quando devolve true, as teclas ficam com a janela em foco (alguém digitando no próprio painel).
        private readonly Func<bool> _isTyping;
        private NativeMethods.LowLevelKeyboardProc _callback;
        private IntPtr _hook;

        public KeyboardCapture(Func<bool> isTyping)
        {
            _isTyping = isTyping;
        }

        // Recebe a tecla da TV (sem "KEY_") a cada tecla apertada.
        public event Action<string> KeyPressed = delegate { };

        public bool IsActive
        {
            get { return _hook != IntPtr.Zero; }
        }

        public void Start()
        {
            if (IsActive)
            {
                return;
            }

            // Guardado num campo para o coletor de lixo não levar o callback enquanto o Windows o usa.
            _callback = OnKey;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.KeyboardLowLevelHook, _callback, NativeMethods.GetModuleHandle(null), 0);
        }

        public void Stop()
        {
            if (!IsActive)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            string key;
            if (code >= 0 && s_keys.TryGetValue(Marshal.ReadInt32(data), out key) && !_isTyping())
            {
                bool isDown = message == (IntPtr)NativeMethods.KeyDownMessage || message == (IntPtr)NativeMethods.SystemKeyDownMessage;
                if (isDown)
                {
                    KeyPressed(key);
                }

                // A tecla não chega à janela em foco.
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        }
    }
}
