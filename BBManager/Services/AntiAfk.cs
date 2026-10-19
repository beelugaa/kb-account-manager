using System.Runtime.InteropServices;
using BBManager.Core;
using BBManager.Models;

namespace BBManager.Services;

/// <summary>
/// Empêche la déconnexion pour inactivité de Roblox (kick après ~20 min sans entrée).
///
/// Roblox ignore les touches envoyées à une fenêtre qui n'a pas le focus (vérifié : une touche Échap postée
/// en arrière-plan n'ouvre pas le menu). La seule façon fiable est donc de donner le focus à la fenêtre
/// environ une demi-seconde, d'appuyer sur une touche, puis de rendre le focus à la fenêtre précédente.
///  - Jump     : touche Espace (le personnage fait un petit saut)
///  - Discreet : touche F15 (aucune action en jeu)
/// </summary>
public static class AntiAfk
{
    private const byte VK_SPACE = 0x20, SC_SPACE = 0x39, VK_F15 = 0x7E, SC_F15 = 0x64;

    // Une seule fenêtre à la fois : deux envois simultanés se voleraient le focus.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Temps écoulé depuis la dernière action de l'utilisateur (clavier / souris).</summary>
    public static TimeSpan UserIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.FromHours(1);
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    public enum AfkResult { Sent, Aborted, NoWindow }

    public static async Task<AfkResult> SendAsync(IntPtr hwnd, AfkMethod method)
    {
        if (hwnd == IntPtr.Zero) return AfkResult.NoWindow;
        await Gate.WaitAsync();
        try
        {
            return method == AfkMethod.Discreet
                ? await ForegroundKeyAsync(hwnd, VK_F15, SC_F15)
                : await ForegroundKeyAsync(hwnd, VK_SPACE, SC_SPACE);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Met la fenêtre au premier plan, appuie sur une touche, puis rend le focus à la fenêtre précédente.
    /// Si l'utilisateur touche au clavier / à la souris pendant l'opération, on annule et on rend le focus aussitôt.
    /// </summary>
    public static async Task<AfkResult> ForegroundKeyAsync(IntPtr hwnd, byte vk, byte scan, bool abortIfUserActive = true)
    {
        var prev = Native.GetForegroundWindow();
        var userTouched = DateTime.Now;                 // repère : toute activité utilisateur postérieure à ce moment = annulation
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        async Task<AfkResult> Restore(AfkResult r)
        {
            if (prev != IntPtr.Zero && prev != hwnd && Native.GetForegroundWindow() == hwnd) await ForegroundAsync(prev);
            return r;
        }
        bool UserActiveSince(DateTime t) => abortIfUserActive && UserIdleTime() < DateTime.Now - t;

        // On n'envoie la touche QUE si la fenêtre Roblox a bien pris le focus (jamais à une autre appli)
        if (!await ForegroundAsync(hwnd)) return AfkResult.NoWindow;
        if (UserActiveSince(userTouched)) return await Restore(AfkResult.Aborted);

        await Task.Delay(400);                          // Roblox n'accepte la touche qu'après avoir réellement pris le focus
        if (Native.GetForegroundWindow() != hwnd) return AfkResult.NoWindow;
        if (UserActiveSince(userTouched)) return await Restore(AfkResult.Aborted);

        Native.keybd_event(vk, scan, 0, UIntPtr.Zero);
        await Task.Delay(160);
        Native.keybd_event(vk, scan, 2, UIntPtr.Zero);
        await Task.Delay(120);
        return await Restore(AfkResult.Sent);
    }

    /// <summary>Donne le focus à une fenêtre (sans simuler Alt) et attend qu'il soit effectif.</summary>
    private static async Task<bool> ForegroundAsync(IntPtr hwnd)
    {
        for (int i = 0; i < 4; i++)
        {
            var fg = Native.GetForegroundWindow();
            if (fg == hwnd) return true;

            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            uint cur = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != cur && Native.AttachThreadInput(cur, fgThread, true);
            try { Native.BringWindowToTop(hwnd); Native.SetForegroundWindow(hwnd); }
            finally { if (attached) Native.AttachThreadInput(cur, fgThread, false); }

            for (int w = 0; w < 10; w++)
            {
                await Task.Delay(30);
                if (Native.GetForegroundWindow() == hwnd) return true;
            }
        }
        return false;
    }
}
