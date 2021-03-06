﻿using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Utils;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;
using VRage;
using VRageRender;
using VRage.FileSystem;
using VRage.Utils;
using VRage.GameServices;
#if SE
using ParallelTasks;
#else
using VRage.Library.Threading;
#endif

using MySteamServiceBase = VRage.Steam.MySteamService;

namespace Phoenix.WorkshopTool
{
    abstract class GameBase
    {
        static MySteamService MySteam { get => (MySteamService)MyServiceManager.Instance.GetService<VRage.GameServices.IMyGameService>(); }

        protected MySandboxGame m_game = null;
        protected MyCommonProgramStartup m_startup;
        protected MySteamServiceBase m_steamService;
        protected static readonly uint AppId = 244850;
        protected static readonly string AppName = "SEWT";
        protected static readonly bool IsME = false;

        static GameBase()
        {
            // Steam API doesn't initialize correctly if it can't find steam_appid.txt
            if (!File.Exists("steam_appid.txt"))
                Directory.SetCurrentDirectory(Path.GetDirectoryName(typeof(FastResourceLock).Assembly.Location) + "\\..");

            var appid = File.ReadAllText($"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}steam_appid.txt");
            AppId = uint.Parse(appid);
#if !SE
            AppName = "MEWT";
            IsME = true;
#endif
            // Override the ExePath, so the game classes can initialize when the exe is outside the game directory
            MyFileSystem.ExePath = new FileInfo(Assembly.GetAssembly(typeof(FastResourceLock)).Location).DirectoryName;
        }

        // Event handler for loading assemblies not in the same directory as the exe.
        // This assumes the current directory contains the assemblies.
        public static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyname = new AssemblyName(args.Name).Name;
            var assemblyPath = Path.Combine(Environment.CurrentDirectory, assemblyname + ".dll");

            if (!File.Exists(assemblyPath))
                assemblyPath = Path.Combine(Environment.CurrentDirectory, "Bin64", assemblyname + ".dll");

            if (!File.Exists(assemblyPath))
                assemblyPath = Path.Combine(Environment.CurrentDirectory, "Bin64", "x64", assemblyname + ".dll");

            if (!File.Exists(assemblyPath))
                assemblyPath = Path.Combine(Environment.CurrentDirectory, "Bin64", assemblyname.Substring(0, assemblyname.LastIndexOf('.')) + ".dll");

            return Assembly.LoadFrom(assemblyPath);
        }

        public virtual int InitGame(string[] args)
        {
            var options = new Options();
            var parser = new CommandLine.Parser(with => with.HelpWriter = Console.Error);

            if (parser.ParseArgumentsStrict(args, options, () => Environment.Exit(1)))
            {
                if (options.ModPaths == null &&
                    options.Blueprints == null &&
#if SE
                    options.IngameScripts == null &&
#endif
                    options.Scenarios == null &&
                    options.Worlds == null &&
                    options.Collections == null)
                {
                    System.Console.WriteLine(CommandLine.Text.HelpText.AutoBuild(options).ToString());
                    return Cleanup(1);
                }

                try
                {
                    // Initialize game code
                    InitSandbox(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), IsME ? "MedievalEngineers": "SpaceEngineers"));
                }
                catch(Exception ex)
                {
                    MySandboxGame.Log.WriteLineAndConsole(string.Format("An exception occurred intializing game libraries: {0}", ex.Message));
                    MySandboxGame.Log.WriteLineAndConsole(ex.StackTrace);
                    return Cleanup(2);
                }

                if (!SteamAPI.IsSteamRunning())
                {
                    MySandboxGame.Log.WriteLineAndConsole("* Steam not detected. Is Steam UAC elevated? *");
                    MySandboxGame.Log.WriteLineAndConsole("* Only compile testing is available. *");
                    MySandboxGame.Log.WriteLineAndConsole("");

                    if (options.Download)
                        return Cleanup(3);

                    options.Upload = false;
                }

                MySandboxGame.Log.WriteLineAndConsole($"{AppName} {Assembly.GetExecutingAssembly().GetName().Version}");

                ParameterInfo[] parameters;
                if (options.Compile)
                {
                    // Init ModAPI
                    var initmethod = typeof(MySandboxGame).GetMethod("InitModAPI", BindingFlags.Instance | BindingFlags.NonPublic);
                    MyDebug.AssertRelease(initmethod != null);

                    if (initmethod != null)
                    {
                        parameters = initmethod.GetParameters();
                        MyDebug.AssertRelease(parameters.Count() == 0);

                        if(!(parameters.Count() == 0))
                            initmethod = null;
                    }

                    if (initmethod != null)
                        initmethod.Invoke(m_game, null);
                    else
                        MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "InitModAPI"));
                }

                ReplaceMethods();

                System.Threading.Tasks.Task<bool> Task;

                if (options.Download)
                    Task = DownloadMods(options);
                else
                    Task = UploadMods(options);

                try
                {
                    // Wait for file transfers to finish (separate thread)
                    while (!Task.Wait(500))
                    {
                        SteamAPI.RunCallbacks();
                    }
                }
                catch(AggregateException ex)
                {
                    MyDebug.AssertRelease(Task.IsFaulted);
                    MyDebug.AssertRelease(ex.InnerException != null);
                    var exception = ex.InnerException;
                    MySandboxGame.Log.WriteLineAndConsole("An exception occurred: " + exception.Message);
                    MySandboxGame.Log.WriteLineAndConsole(exception.StackTrace);
                    return Cleanup(4);
                }
                catch(Exception ex)
                {
                    MySandboxGame.Log.WriteLineAndConsole("An exception occurred: " + ex.Message);
                    MySandboxGame.Log.WriteLineAndConsole(ex.StackTrace);
                    return Cleanup(5);
                }

                // If the task reported any error, return exit code
                if (!Task.Result)
                    return Cleanup(-1);
            }

            return Cleanup();
        }

        void ReplaceMethods()
        {
            ReplaceMethod(typeof(VRage.Steam.MySteamWorkshopItemPublisher), "UpdatePublishedItem", BindingFlags.Instance | BindingFlags.NonPublic, typeof(InjectedMethod), "UpdatePublishedItem", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>
        /// Replaces a method with another one
        /// </summary>
        /// <param name="sourceType">Original type</param>
        /// <param name="sourceMethod">Original method name</param>
        /// <param name="destinationType">New type</param>
        /// <param name="destinationMethod">New method name</param>
        void ReplaceMethod(Type sourceType, string sourceMethod, BindingFlags sourceBinding, Type destinationType, string destinationMethod, BindingFlags destinationBinding)
        {
            ParameterInfo[] sourceParameters;
            ParameterInfo[] destinationParameters;
            var methodtoreplace = sourceType.GetMethod(sourceMethod, sourceBinding);
            var methodtoinject = destinationType.GetMethod(destinationMethod, destinationBinding);

            MyDebug.AssertRelease(methodtoreplace != null);
            if (methodtoreplace != null && methodtoreplace != null)
            {
                sourceParameters = methodtoreplace.GetParameters();
                destinationParameters = methodtoinject.GetParameters();
                MyDebug.AssertDebug(sourceParameters.Length == destinationParameters.Length);
                bool valid = true;

                // Verify signatures
                for (var x = 0; x < Math.Min(destinationParameters.Length, sourceParameters.Length); x++)
                {
                    MyDebug.AssertDebug(destinationParameters[x].ParameterType == sourceParameters[x].ParameterType);
                    if (destinationParameters[x].ParameterType != sourceParameters[x].ParameterType)
                        valid = false;
                }

                if (sourceParameters.Length != destinationParameters.Length || !valid)
                    methodtoreplace = null;
            }

            if (methodtoreplace != null && methodtoinject != null)
                MethodUtil.ReplaceMethod(methodtoreplace, methodtoinject);
            else
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, sourceMethod));
        }

        // Returns argument for chaining
        private int Cleanup(int errorCode = 0)
        {
            CleanupSandbox();
#if !SE
            Environment.Exit(errorCode);
#endif
            return errorCode;
        }

#region Sandbox stuff
        private void CleanupSandbox()
        {
            m_steamService?.Dispose();
            m_game?.Dispose();
            m_steamService = null;
            m_game = null;
        }

        protected abstract bool SetupBasicGameInfo();
        protected abstract MySandboxGame InitGame();

        // This is mostly copied from MyProgram.Main(), with UI stripped out.
        protected virtual void InitSandbox(string instancepath)
        {
            // Infinario was removed from SE in update 1.184.6, but is still in ME
            var infinario = typeof(MyFakes).GetField("ENABLE_INFINARIO");

            if (infinario != null)
                infinario.SetValue(null, false);
            
            if (m_game != null)
                m_game.Exit();

            if (!SetupBasicGameInfo())
                return;

            if (System.Diagnostics.Debugger.IsAttached)
                m_startup.CheckSteamRunning();        // Just give the warning message box when debugging, ignore for release

#if SE
            if (!MySandboxGame.IsDedicated)
                MyFileSystem.InitUserSpecific(m_steamService.UserId.ToString());
#endif

            try
            {
                // Init null render so profiler-enabled builds don't crash
                var render = new MyNullRender();
                MyRenderProxy.Initialize(render);
#if !SE
                MyRenderProxy.GetRenderProfiler().SetAutocommit(false);
                MyRenderProxy.GetRenderProfiler().InitMemoryHack("MainEntryPoint");
#endif
                // NOTE: an assert may be thrown in debug, about missing Tutorials.sbx. Ignore it.
                m_game = InitGame();

                // Initializing the workshop means the categories are available
                var initWorkshopMethod = m_game.GetType().GetMethod("InitSteamWorkshop", BindingFlags.NonPublic | BindingFlags.Instance);
                MyDebug.AssertRelease(initWorkshopMethod != null);

                if (initWorkshopMethod != null)
                {
                    var parameters = initWorkshopMethod.GetParameters();
                    MyDebug.AssertRelease(parameters.Count() == 0);
                }

                if (initWorkshopMethod != null)
                    initWorkshopMethod.Invoke(m_game, null);
                else
                    MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "InitSteamWorkshop"));
            }
            catch (Exception ex)
            {
                // This shouldn't fail, but don't stop even if it does
                MySandboxGame.Log.WriteLineAndConsole("An exception occured, ignoring: " + ex.Message);
            }

        }
#endregion

#region Upload
        static System.Threading.Tasks.Task<bool> UploadMods(Options options)
        {
            MySandboxGame.Log.WriteLineAndConsole(string.Empty);

            var Task = System.Threading.Tasks.Task<bool>.Factory.StartNew(() =>
            {
                bool success = true;
                MySandboxGame.Log.WriteLineAndConsole("Beginning batch workshop upload...");
                MySandboxGame.Log.WriteLineAndConsole(string.Empty);
                List<string> itemPaths;

                // Process mods
                itemPaths = GetGlobbedPaths(TestPathAndMakeAbsolute(WorkshopType.Mod, options.ModPaths));
                if (!ProcessItemsUpload(WorkshopType.Mod, itemPaths, options))
                    success = false;

                // Process blueprints
                itemPaths = GetGlobbedPaths(TestPathAndMakeAbsolute(WorkshopType.Blueprint, options.Blueprints));
                if (!ProcessItemsUpload(WorkshopType.Blueprint, itemPaths, options))
                    success = false;
#if SE
                // Process ingame scripts
                itemPaths = GetGlobbedPaths(TestPathAndMakeAbsolute(WorkshopType.IngameScript, options.IngameScripts));
                if (!ProcessItemsUpload(WorkshopType.IngameScript, itemPaths, options))
                    success = false;
#endif
                // Process worlds
                itemPaths = GetGlobbedPaths(TestPathAndMakeAbsolute(WorkshopType.World, options.Worlds));
                if (!ProcessItemsUpload(WorkshopType.World, itemPaths, options))
                    success = false;

                // Process scenarios
                itemPaths = GetGlobbedPaths(TestPathAndMakeAbsolute(WorkshopType.Scenario, options.Scenarios));
                if (!ProcessItemsUpload(WorkshopType.Scenario, itemPaths, options))
                    success = false;

                MySandboxGame.Log.WriteLineAndConsole("Batch workshop upload complete!");
                return success;
            });

            return Task;
        }

        static bool ProcessItemsUpload(WorkshopType type, List<string> paths, Options options)
        {
            bool success = true;
            for (int idx = 0; idx < paths.Count; idx++)
            {
                var pathname = Path.GetFullPath(paths[idx]);
                var tags = options.Tags;

                // If user comma-separated the tags, split them
                if(tags != null && tags.Length == 1)
                {
                    tags = tags[0].Split(',', ';');
                }

                var mod = new Uploader(type, pathname, tags, options.ExcludeExtensions, options.Compile, options.DryRun, options.Development, options.Visibility, options.Force, options.Thumbnail);
                if (options.UpdateOnly && mod.ModId == 0)
                {
                    MySandboxGame.Log.WriteLineAndConsole(string.Format("--update-only passed, skipping: {0}", mod.Title));
                    continue;
                }
                MySandboxGame.Log.WriteLineAndConsole(string.Format("Processing {0}: {1}", type.ToString(), mod.Title));

                if (mod.Compile())
                {
                    if (options.Upload)
                    {
                        if (mod.Publish())
                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Complete: {0}", mod.Title));
                        else
                        {
                            success = false;
                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Error occurred: {0}", mod.Title));
                        }
                    }
                    else
                    {
                        MySandboxGame.Log.WriteLineAndConsole(string.Format("Not uploading: {0}", mod.Title));
                        mod.UpdatePreviewFileOrTags();
                        MySandboxGame.Log.WriteLineAndConsole(string.Format("Complete: {0}", mod.Title));
                    }
                }
                else
                {
                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Skipping {0}: {1}", type.ToString(), mod.Title));
                    success = false;
                }

                MySandboxGame.Log.WriteLineAndConsole(string.Empty);
            }
            return success;
        }
#endregion  Upload

#region Download
        static System.Threading.Tasks.Task<bool> DownloadMods(Options options)
        {
            // Get PublishItemBlocking internal method via reflection
            MySandboxGame.Log.WriteLineAndConsole(string.Empty);

            var Task = System.Threading.Tasks.Task<bool>.Factory.StartNew(() =>
            {
                bool success = true;

                MySandboxGame.Log.WriteLineAndConsole("Beginning batch workshop download...");
                MySandboxGame.Log.WriteLineAndConsole(string.Empty);

                if (options.Collections?.Count() > 0)
                {
                    var items = new List<MyWorkshopItem>();

                    // get collection information
                    options.Collections.ForEach(s => items.AddRange(WorkshopHelper.GetCollectionDetails(ulong.Parse(s))));

                    options.ModPaths = CombineCollectionWithList(WorkshopType.Mod, items, options.ModPaths);
                    options.Blueprints = CombineCollectionWithList(WorkshopType.Blueprint, items, options.Blueprints);
#if SE
                    options.IngameScripts = CombineCollectionWithList(WorkshopType.IngameScript, items, options.IngameScripts);
#endif
                    options.Worlds = CombineCollectionWithList(WorkshopType.World, items, options.Worlds);
                    options.Scenarios = CombineCollectionWithList(WorkshopType.Scenario, items, options.Scenarios);
                }

                if (!ProcessItemsDownload(WorkshopType.Mod, options.ModPaths, options))
                    success = false;
                if (!ProcessItemsDownload(WorkshopType.Blueprint, options.Blueprints, options))
                    success = false;
#if SE
                if (!ProcessItemsDownload(WorkshopType.IngameScript, options.IngameScripts, options))
                    success = false;
#endif
                if (!ProcessItemsDownload(WorkshopType.World, options.Worlds, options))
                    success = false;
                if (!ProcessItemsDownload(WorkshopType.Scenario, options.Scenarios, options))
                    success = false;

                MySandboxGame.Log.WriteLineAndConsole("Batch workshop download complete!");
                return success;
            });

            return Task;
        }

        static bool ProcessItemsDownload(WorkshopType type, string[] paths, Options options)
        {
            if (paths == null)
                return true;

            var items = new List<MyWorkshopItem>();
            var modids = paths.Select(ulong.Parse);

            MySandboxGame.Log.WriteLineAndConsole(string.Format("Processing {0}s...", type.ToString()));

            var downloadPath = WorkshopHelper.GetWorkshopItemPath(type);

#if SE
            if (MyWorkshop.GetItemsBlockingUGC(modids, items))
#else
            if (MyWorkshop.GetItemsBlocking(modids, items))
#endif
            {
                bool success = false;
                if (type == WorkshopType.Mod)
                {
#if SE
                    var result = MyWorkshop.DownloadModsBlockingUGC(items, null);
#else
                    var result = MyWorkshop.DownloadModsBlocking(items);
#endif
                    success = result.Success;
                }
                else
                {
                    if (type == WorkshopType.Blueprint)
                    {
                        var loopsuccess = false;
                        foreach (var item in items)
                        {
#if SE
                            loopsuccess = MyWorkshop.DownloadBlueprintBlockingUGC(item);
#else
                            loopsuccess = MyWorkshop.DownloadBlueprintBlocking(item);
#endif
                            if (!loopsuccess)
                                MySandboxGame.Log.WriteLineAndConsole(string.Format("Download of {0} FAILED!", item.Id));
                            else
                                success = true;
                        }
                    }
#if SE
                    else if (type == WorkshopType.IngameScript)
                    {
                        var loopsuccess = false;
                        foreach (var item in items)
                        {
                            loopsuccess = MyWorkshop.DownloadScriptBlocking(item);
                            if (!loopsuccess)
                                MySandboxGame.Log.WriteLineAndConsole(string.Format("Download of {0} FAILED!", item.Id));
                            else
                                success = true;
                        }
                    }
#endif
#if SE
                    else if (type == WorkshopType.World || type == WorkshopType.Scenario)
                    {
                        var loopsuccess = false;
                        string path;
                        MyWorkshop.MyWorkshopPathInfo pathinfo = type == WorkshopType.World ?
                                                                MyWorkshop.MyWorkshopPathInfo.CreateWorldInfo() :
                                                                MyWorkshop.MyWorkshopPathInfo.CreateScenarioInfo();

                        foreach (var item in items)
                        {
                            // This downloads and extracts automatically, no control over it
                            loopsuccess = MyWorkshop.TryCreateWorldInstanceBlocking(item, pathinfo, out path, false);
                            if (!loopsuccess)
                            {
                                MySandboxGame.Log.WriteLineAndConsole(string.Format("Download of {0} FAILED!", item.Id));
                            }
                            else
                            {
                                MySandboxGame.Log.WriteLineAndConsole(string.Format("Downloaded '{0}' to {1}", item.Title, path));
                                success = true;
                            }
                        }
                    }
#endif
                    else
                    {
                        throw new NotSupportedException(string.Format("Downloading of {0} not yet supported.", type.ToString()));
                    }
                }

                if (success)
                {
                    MySandboxGame.Log.WriteLineAndConsole("Download success!");
                }
                else
                {
                    MySandboxGame.Log.WriteLineAndConsole("Download FAILED!");
                    return false;
                }

                foreach (var item in items)
                {
                    MySandboxGame.Log.WriteLineAndConsole(string.Format("{0} '{1}' tags: {2}", item.Id, item.Title, string.Join(", ", item.Tags)));
                    if (options.Extract)
                    {
                        var mod = new Downloader(downloadPath, item);
                        mod.Extract();
                    }
                    MySandboxGame.Log.WriteLineAndConsole(string.Empty);
                }
            }
            return true;
        }
#endregion Download

#region Pathing
        static string[] TestPathAndMakeAbsolute(WorkshopType type, string[] paths)
        {
            for (int idx = 0; paths != null && idx < paths.Length; idx++)
            {
                // If the passed in path doesn't exist, and is relative, try to match it with the expected data directory
                if (!Directory.Exists(paths[idx]) && !Path.IsPathRooted(paths[idx]))
                    paths[idx] = Path.Combine(WorkshopHelper.GetWorkshopItemPath(type), paths[idx]);
            }
            return paths;
        }

        /// <summary>
        /// Processes list of files, and returns a glob expanded list.
        /// </summary>
        /// <param name="paths"></param>
        /// <returns></returns>
        static List<string> GetGlobbedPaths(string[] paths)
        {
            List<string> itemPaths = new List<string>();

            if (paths == null)
                return itemPaths;

            foreach (var path in paths)
            {
                var dirs = Directory.EnumerateDirectories(Path.GetDirectoryName(path), Path.GetFileName(path));
                
                if(dirs.Count() == 0)
                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Directory not found, skipping: {0}", path));

                itemPaths.AddList(dirs
                    .Where(i => !(Path.GetFileName(i).StartsWith(".") ||                // Ignore directories starting with "." (eg. ".vs")
                                Path.GetFileName(i).StartsWith(Constants.SEWT_Prefix))) // also ignore directories starting with "[_SEWT_]" (downloaded by this mod)
                            .Select(i => i).ToList());
            }
            return itemPaths;
        }
#endregion Pathing
        static string[] CombineCollectionWithList(WorkshopType type, List<MyWorkshopItem> items, string[] existingitems)
        {
            var tempList = new List<string>();

            // Check mods
            items.Where(i => i.Tags.Contains(type.ToString(), StringComparer.InvariantCultureIgnoreCase))
                                .ForEach(i => tempList.Add(
                                    i.Id.ToString()
                                    ));

            if (tempList.Count > 0)
            {
                if(existingitems != null)
                    tempList.AddArray(existingitems);

                return tempList.ToArray();
            }
            return existingitems;
        }
    }
}
