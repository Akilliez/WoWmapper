﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WoWmapper.Classes;
using WoWmapper.Offsets;
using WoWmapper.Properties;

namespace WoWmapper.WorldOfWarcraft
{
    public static class ProcessWatcher
    {
#region Native Methods and Structs
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr hProcess,
            IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process([In] IntPtr process, [Out] out bool wow64Process);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();


        private const int PROCESS_WM_READ = 0x0010;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;        // x position of upper-left corner
            public int Top;         // y position of upper-left corner
            public int Right;       // x position of lower-right corner
            public int Bottom;      // y position of lower-right corner
        }
#endregion Native Methods and Structs
        private static IntPtr _gameHandle = IntPtr.Zero; // Handle for reading memory
        private static Process _gameProcess; // Current instance of WoW
        private static Thread _watcherThread; // Thread for monitoring WoW instances
        private static bool _watching; // Current state of the watcher
        private static MemoryReader.PlayerInfo _playerInfo;

        public static bool CanReadMemory => _gameHandle != IntPtr.Zero && GameArchitecture == Enums.GameArchitecture.X64 && Settings.Default.EnableAdvancedFeatures && OffsetManager.OffsetsAvailable;
        public static bool GameRunning => _gameProcess != null;
        public static bool IsRunning => _watching;

        public static Enums.GameArchitecture GameArchitecture
        {
            get
            {
                if (_gameProcess == null) return Enums.GameArchitecture.None;
                try
                {
                    bool proc64;
                    var x = IsWow64Process(_gameProcess.Handle, out proc64);
                    return proc64 ? Enums.GameArchitecture.X86 : Enums.GameArchitecture.X64;
                } catch { }
                return Enums.GameArchitecture.None;
            }
        }

        public static void Start()
        {
            if (_watching || _watcherThread != null) return;
            Logger.Write("Starting process watcher...");

            _watching = true;

            _watcherThread = new Thread(WatcherThread) { IsBackground = true };
            _watcherThread.Start();
        }

        public static void Stop()
        {
            if (!_watching || _watcherThread == null) return;
            Logger.Write("Stopping process watcher...");
            _watcherThread?.Abort();
            _gameProcess?.Dispose();
            _watcherThread = null;
            _gameHandle = IntPtr.Zero;
            _watching = false;
        }

        public static void Restart()
        {
            Stop();
            Start();
        }

        private static void ResetProcess()
        {
            _gameProcess?.Dispose();
            _gameProcess = null;
            _gameHandle = IntPtr.Zero;
        }

        private static void WatcherThread()
        {
            while (_watching)
            {
                // Check current process
                if (_gameProcess != null)
                {
                    try
                    {
                        if (_gameProcess.HasExited || !IsWindow(_gameProcess.MainWindowHandle))
                        {
                            Logger.Write("WoW process invalidated, clearing!");
                            ResetProcess();
                        }
                        else
                        {
                            if (CanReadMemory) _playerInfo = MemoryReader.GetPlayerInfo();
                        }
                    }
                    catch
                    {
                        ResetProcess();
                    }
                }

                // Find game process
                if (_gameProcess == null)
                {
                    var wowProcess = GetWowProcesses();
                    Process activeProcess = null;
                    try
                    {
                        activeProcess =
                        wowProcess.FirstOrDefault(proc => proc.HasExited == false && proc.MainWindowHandle != IntPtr.Zero);
                        if(activeProcess != null) Logger.Write("Found WoW process! Handle is {0}", activeProcess.Handle);
                    } catch { }
                    

                    if (activeProcess != null)
                    {
                        Logger.Write("Opening process memory for reading...");
                        // Open process memory for advanced features
                        if (Settings.Default.EnableAdvancedFeatures)
                        {
                            _gameHandle = OpenProcess(PROCESS_WM_READ, false, activeProcess.Id);

                            // Check current game build
                            var gameBuild = activeProcess.MainModule.FileVersionInfo.ProductPrivatePart;
                            var offsetsLoaded = OffsetManager.InitializeOffsets(gameBuild);

                            if (offsetsLoaded)
                                Logger.Write($"Process memory opened (Handle={_gameHandle}), offsets loaded for build {gameBuild}");
                            else
                            {
                                Logger.Write("Failed to load appropriate offsets, memory reading disabled");
                                _gameHandle = IntPtr.Zero;
                            }
                                
                        }
                        
                        _gameProcess = activeProcess;
                    }
                }

                Thread.Sleep(1000);
            }
        }

        public static MemoryReader.PlayerInfo CurrentPlayerInfo => _playerInfo;

        public static RECT GameWindowRect
        {
            get
            {
                if (_gameProcess == null || !IsWindow(_gameProcess.MainWindowHandle)) return new RECT();

                RECT windowRect;
                var gotRect = GetWindowRect(_gameProcess.MainWindowHandle, out windowRect);
                return gotRect ? windowRect : new RECT();
            }
        }
        public static class MemoryReader
        {
            public static GameInfo.GameState GetGameState()
            {
                if(CanReadMemory)
                    try
                    {
                        byte[] bState = new byte[1];
                        int bytesRead = 0;
                        ReadProcessMemory(_gameHandle,
                            _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.GameState), bState,
                            bState.Length, ref bytesRead);
                        return (GameInfo.GameState) bState[0];
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Exception reading game state! {0}", ex);
                    }
                return GameInfo.GameState.NotRunning;
            }

            public static string GetPlayerName()
            {
                if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return String.Empty;

                try
                {
                    byte[] bName = new byte[12];
                    int bytesRead = 0;
                    ReadProcessMemory(_gameHandle,
                        _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.PlayerName), bName,
                        bName.Length, ref bytesRead);
                    var length = Array.FindIndex(bName, item => item == 0x00);
                    if (length > 0)
                        return Encoding.Default.GetString(bName, 0, length);
                    else
                        return Encoding.Default.GetString(bName, 0, bName.Length);
                }
                catch (Exception ex)
                {
                    Logger.Write("Exception reading player name! {0}", ex);
                }
                return String.Empty;
            }

            public static GameInfo.Classes GetPlayerClass()
            {
                if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return GameInfo.Classes.None;

                if (CanReadMemory)
                    try
                    {
                        byte[] bClass = new byte[1];
                        int bytesRead = 0;
                        ReadProcessMemory(_gameHandle,
                            _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.PlayerClass), bClass,
                            bClass.Length, ref bytesRead);
                        return (GameInfo.Classes) bClass[0];
                    }
                    catch (Exception ex) { Logger.Write("Exception reading player class! {0}", ex); }
                return GameInfo.Classes.None;
            }

            public static PlayerInfo GetPlayerInfo()
            {
                try
                {
                    if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return null;

                    var playerBase = GetPlayerBase();

                    if (playerBase == IntPtr.Zero) return null;

                    var level = new byte[1];
                    var currentHp = new byte[4];
                    var maxHp = new byte[4];

                    var bytesRead = 0;

                    ReadProcessMemory(_gameHandle, playerBase + OffsetManager.GetOffset(OffsetType.PlayerLevel), level,
                        level.Length, ref bytesRead);
                    ReadProcessMemory(_gameHandle, playerBase + OffsetManager.GetOffset(OffsetType.PlayerHealthCurrent),
                        currentHp, currentHp.Length, ref bytesRead);
                    ReadProcessMemory(_gameHandle, playerBase + OffsetManager.GetOffset(OffsetType.PlayerHealthMax),
                        maxHp, maxHp.Length, ref bytesRead);

                    return new PlayerInfo()
                    {
                        Level = level[0],
                        CurrentHealth = BitConverter.ToUInt32(currentHp, 0),
                        MaxHealth = BitConverter.ToUInt32(maxHp, 0)
                    };
                }
                catch (Exception ex)
                {
                    Logger.Write("Exception reading player info! {0}", ex);
                }
                return null;
            }

            public static bool GetMouselooking()
            {
                try
                {
                    if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return false;

                    int bytesRead = 0;
                    byte[] ptrMouseLook = new byte[8];
                    byte[] isMouseLooking = new byte[1];

                    // Read the location of the mouse info from memory
                    ReadProcessMemory(_gameHandle,
                        _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.MouseLook), ptrMouseLook,
                        ptrMouseLook.Length, ref bytesRead);

                    // Read the byte that represents mouselook
                    ReadProcessMemory(_gameHandle, (IntPtr) BitConverter.ToInt64(ptrMouseLook, 0) + 4, isMouseLooking,
                        isMouseLooking.Length, ref bytesRead);

                    if (((int) isMouseLooking[0] & 1) == 1) return true;
                }
                catch (Exception ex)
                {
                    Logger.Write("Exception reading mouselook state! {0}", ex);
                }
                
                return false;
            }

            public static byte[] GetTargetGuid()
            {
                try
                {
                    if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return null;

                    byte[] targetGuid = new byte[16];
                    int bytesRead = 0;
                    ReadProcessMemory(_gameHandle,
                        _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.TargetGuid), targetGuid,
                        targetGuid.Length, ref bytesRead);
                    return targetGuid;
                }
                catch (Exception ex)
                {
                    Logger.Write("Exception reading target guid! {0}", ex);
                }
                return null;
            }

            public static byte[] GetMouseGuid()
            {
                try
                {
                    if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return null;

                    byte[] mouseGuid = new byte[16];
                    int bytesRead = 0;
                    ReadProcessMemory(_gameHandle,
                        _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.MouseGuid), mouseGuid,
                        mouseGuid.Length, ref bytesRead);
                    return mouseGuid;
                }
                catch (Exception ex)
                {
                    Logger.Write("Exception reading mouseover guid! {0}", ex);
                }
                return null;
            }

            private static IntPtr GetPlayerBase()
            {
                if (!CanReadMemory || GetGameState() != GameInfo.GameState.LoggedIn) return IntPtr.Zero;

                try
                {
                    byte[] pointer = new byte[8];
                    int bytesRead = 0;
                    ReadProcessMemory(_gameHandle,
                        _gameProcess.MainModule.BaseAddress + OffsetManager.GetOffset(OffsetType.PlayerBase), pointer,
                        pointer.Length, ref bytesRead);

                    IntPtr playerPointer = (IntPtr)BitConverter.ToInt64(pointer, 0);

                    ReadProcessMemory(_gameHandle, playerPointer + 0x08, pointer, pointer.Length, ref bytesRead);

                    IntPtr playerBase = (IntPtr)BitConverter.ToInt64(pointer, 0);
                    return playerBase; 
                }
                catch (Exception ex) { Logger.Write("Exception reading player base! {0}", ex); }
                return IntPtr.Zero;
            }

            public class PlayerInfo
            {
                public uint Level { get; set; }
                public uint CurrentHealth { get; set; }
                public uint MaxHealth { get; set; }
            }
        }

        public static class Interaction
        {
            public static void SendKeyDown(Key key)
            {
                if (Settings.Default.SendForegroundKeys)
                {
                    // Get foreground window and send message
                    var hWndFg = GetForegroundWindow();
                    if (hWndFg == IntPtr.Zero)
                    {
                        Logger.Write("Failed to get foreground window handle");
                        return;
                    }

                    var foregroundResult = SendMessage(hWndFg, WM_KEYDOWN, (IntPtr)KeyInterop.VirtualKeyFromKey(key), IntPtr.Zero);
                    if (foregroundResult != 0)
                        Logger.Write("SendMessage WM_KEYDOWN returned error code: {0}", foregroundResult);
                    return;
                }

                if (!ProcessWatcher.GameRunning) return;

                var sendResult = SendMessage(_gameProcess.MainWindowHandle, WM_KEYDOWN, (IntPtr)KeyInterop.VirtualKeyFromKey(key), IntPtr.Zero);
                if (sendResult != 0)
                    Logger.Write("SendMessage WM_KEYDOWN returned error code: {0}", sendResult);
            }

            public static void SendKeyUp(Key key)
            {
                if (Settings.Default.SendForegroundKeys)
                {
                    // Get foreground window and send message
                    var hWndFg = GetForegroundWindow();
                    if (hWndFg == IntPtr.Zero)
                    {
                        Logger.Write("Failed to get foreground window handle");
                        return;
                    }

                    var foregroundResult = SendMessage(hWndFg, WM_KEYUP, (IntPtr)KeyInterop.VirtualKeyFromKey(key), IntPtr.Zero);
                    if (foregroundResult != 0)
                        Logger.Write("SendMessage WM_KEYUP returned error code: {0}", foregroundResult);
                    return;
                }

                if (!ProcessWatcher.GameRunning) return;

                var sendResult = SendMessage(_gameProcess.MainWindowHandle, WM_KEYUP, (IntPtr)KeyInterop.VirtualKeyFromKey(key), IntPtr.Zero);
                if (sendResult != 0)
                    Logger.Write("SendMessage WM_KEYUP returned error code: {0}", sendResult);
            }
        }

        private static Process[] GetWowProcesses()
        {
            // Build list of process names to search
            var searchNames = new List<string>();
            if(Settings.Default.ForceArchitecture != 64) searchNames.Add("wow");
            if(Settings.Default.ForceArchitecture != 32) searchNames.Add("wow-64");

            // Build list of matching processes
            var processes = Process.GetProcesses();
            return processes.Where(proc => searchNames.Contains(proc.ProcessName.ToLower())).ToArray();
        }
    }
}
