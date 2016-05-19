﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WindowsInput;
using WindowsInput.Native;
using Wox.Infrastructure.Hotkey;
using Wox.Infrastructure.Logger;
using Wox.Infrastructure.Storage;
using Control = System.Windows.Controls.Control;

namespace Wox.Plugin.CMD
{
    public class CMD : IPlugin, ISettingProvider, IPluginI18n, IContextMenu
    {
        private PluginInitContext context;
        private bool WinRStroked;
        private readonly KeyboardSimulator keyboardSimulator = new KeyboardSimulator(new InputSimulator());

        private readonly Settings _settings;
        private readonly PluginJsonStorage<Settings> _storage;

        public CMD()
        {
            _storage = new PluginJsonStorage<Settings>();
            _settings = _storage.Load();
        }

        ~CMD()
        {
            _storage.Save();
        }

        public List<Result> Query(Query query)
        {
            List<Result> results = new List<Result>();
            string cmd = query.Search;
            if (string.IsNullOrEmpty(cmd))
            {
                return ResultsFromlHistory();
            }
            else
            {
                var queryCmd = GetCurrentCmd(cmd);
                results.Add(queryCmd);
                var history = GetHistoryCmds(cmd, queryCmd);
                results.AddRange(history);

                try
                {
                    string basedir = null;
                    string dir = null;
                    string excmd = Environment.ExpandEnvironmentVariables(cmd);
                    if (Directory.Exists(excmd) && (cmd.EndsWith("/") || cmd.EndsWith(@"\")))
                    {
                        basedir = excmd;
                        dir = cmd;
                    }
                    else if (Directory.Exists(Path.GetDirectoryName(excmd) ?? string.Empty))
                    {
                        basedir = Path.GetDirectoryName(excmd);
                        var dirn = Path.GetDirectoryName(cmd);
                        dir = (dirn.EndsWith("/") || dirn.EndsWith(@"\")) ? dirn : cmd.Substring(0, dirn.Length + 1);
                    }

                    if (basedir != null)
                    {
                        var autocomplete = Directory.GetFileSystemEntries(basedir).
                            Select(o => dir + Path.GetFileName(o)).
                            Where(o => o.StartsWith(cmd, StringComparison.OrdinalIgnoreCase) &&
                                       !results.Any(p => o.Equals(p.Title, StringComparison.OrdinalIgnoreCase)) &&
                                       !results.Any(p => o.Equals(p.Title, StringComparison.OrdinalIgnoreCase))).ToList();
                        autocomplete.Sort();
                        results.AddRange(autocomplete.ConvertAll(m => new Result
                        {
                            Title = m,
                            IcoPath = "Images/cmd.png",
                            Action = c =>
                            {
                                ExecuteCommand(m);
                                return true;
                            }
                        }));
                    }
                }
                catch (Exception e)
                {
                    Log.Exception(e);
                }
                return results;
            }
        }

        private List<Result> GetHistoryCmds(string cmd, Result result)
        {
            IEnumerable<Result> history = _settings.Count.Where(o => o.Key.Contains(cmd))
                .OrderByDescending(o => o.Value)
                .Select(m =>
                {
                    if (m.Key == cmd)
                    {
                        result.SubTitle = string.Format(context.API.GetTranslation("wox_plugin_cmd_cmd_has_been_executed_times"), m.Value);
                        return null;
                    }

                    var ret = new Result
                    {
                        Title = m.Key,
                        SubTitle = string.Format(context.API.GetTranslation("wox_plugin_cmd_cmd_has_been_executed_times"), m.Value),
                        IcoPath = "Images/cmd.png",
                        Action = c =>
                        {
                            ExecuteCommand(m.Key);
                            return true;
                        }
                    };
                    return ret;
                }).Where(o => o != null).Take(4);
            return history.ToList();
        }

        private Result GetCurrentCmd(string cmd)
        {
            Result result = new Result
            {
                Title = cmd,
                Score = 5000,
                SubTitle = context.API.GetTranslation("wox_plugin_cmd_execute_through_shell"),
                IcoPath = "Images/cmd.png",
                Action = c =>
                {
                    ExecuteCommand(cmd);
                    return true;
                }
            };

            return result;
        }

        private List<Result> ResultsFromlHistory()
        {
            IEnumerable<Result> history = _settings.Count.OrderByDescending(o => o.Value)
                .Select(m => new Result
                {
                    Title = m.Key,
                    SubTitle = string.Format(context.API.GetTranslation("wox_plugin_cmd_cmd_has_been_executed_times"), m.Value),
                    IcoPath = "Images/cmd.png",
                    Action = c =>
                    {
                        ExecuteCommand(m.Key);
                        return true;
                    }
                }).Take(5);
            return history.ToList();
        }

        private void ExecuteCommand(string command, bool runAsAdministrator = false)
        {
            command = command.Trim();
            command = Environment.ExpandEnvironmentVariables(command);

            ProcessStartInfo info;
            if (_settings.Shell == Shell.CMD)
            {
                var arguments = _settings.LeaveShellOpen ? $"/k \"{command}\"" : $"/c \"{command}\" & pause";
                info = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = arguments,
                };
            }
            else if (_settings.Shell == Shell.RunCommand)
            {
                var parts = command.Split(new[] { ' ' }, 2);
                if (parts.Length == 1)
                {
                    info = new ProcessStartInfo(command);
                }
                else
                {
                    var filename = parts[0];
                    if (ExistInPath(filename))
                    {
                        info = new ProcessStartInfo(command);
                    }
                    else
                    {
                        var arguemtns = parts[1];
                        info = new ProcessStartInfo
                        {
                            FileName = filename,
                            Arguments = arguemtns
                        };
                    }
                }
            }
            else
            {
                string arguments;
                if (_settings.LeaveShellOpen)
                {
                    arguments = $"-NoExit \"{command}\"";
                }
                else
                {
                    arguments = $"\"{command} ; Read-Host -Prompt \\\"Press Enter to continue\\\"\"";
                }
                info = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments
                };
            }

            info.UseShellExecute = true;
            info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            info.Verb = runAsAdministrator ? "runas" : "";

            try
            {
                Process.Start(info);
                _settings.AddCmdHistory(command);
            }
            catch (FileNotFoundException e)
            {
                MessageBox.Show($"Command not found: {e.Message}");
            }
        }

        private bool ExistInPath(string filename)
        {
            if (File.Exists(filename))
            {
                return true;
            }
            else
            {
                var values = Environment.GetEnvironmentVariable("PATH");
                if (values != null)
                {
                    foreach (var path in values.Split(';'))
                    {
                        var path1 = Path.Combine(path, filename);
                        var path2 = Path.Combine(path, filename + ".exe");
                        if (File.Exists(path1) || File.Exists(path2))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                else
                {
                    return false;
                }
            }
        }

        public void Init(PluginInitContext context)
        {
            this.context = context;
            context.API.GlobalKeyboardEvent += API_GlobalKeyboardEvent;
        }

        bool API_GlobalKeyboardEvent(int keyevent, int vkcode, SpecialKeyState state)
        {
            if (_settings.ReplaceWinR)
            {
                if (keyevent == (int)KeyEvent.WM_KEYDOWN && vkcode == (int)Keys.R && state.WinPressed)
                {
                    WinRStroked = true;
                    OnWinRPressed();
                    return false;
                }
                if (keyevent == (int)KeyEvent.WM_KEYUP && WinRStroked && vkcode == (int)Keys.LWin)
                {
                    WinRStroked = false;
                    keyboardSimulator.ModifiedKeyStroke(VirtualKeyCode.LWIN, VirtualKeyCode.CONTROL);
                    return false;
                }
            }
            return true;
        }

        private void OnWinRPressed()
        {
            context.API.ShowApp();
            context.API.ChangeQuery($"{context.CurrentPluginMetadata.ActionKeywords[0]}{Plugin.Query.TermSeperater}");
        }

        public Control CreateSettingPanel()
        {
            return new CMDSetting(_settings);
        }

        public string GetTranslatedPluginTitle()
        {
            return context.API.GetTranslation("wox_plugin_cmd_plugin_name");
        }

        public string GetTranslatedPluginDescription()
        {
            return context.API.GetTranslation("wox_plugin_cmd_plugin_description");
        }

        public bool IsInstantQuery(string query) => false;

        public List<Result> LoadContextMenus(Result selectedResult)
        {
            return new List<Result>
            {
                        new Result
                        {
                            Title = context.API.GetTranslation("wox_plugin_cmd_run_as_administrator"),
                            Action = c =>
                            {
                                context.API.HideApp();
                                ExecuteCommand(selectedResult.Title, true);
                                return true;
                            },
                            IcoPath = "Images/cmd.png"
                        }
                     };
        }
    }
}