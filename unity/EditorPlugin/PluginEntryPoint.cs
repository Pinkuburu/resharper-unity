using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using JetBrains.Annotations;
using JetBrains.DataFlow;
using JetBrains.Platform.RdFramework;
using JetBrains.Platform.RdFramework.Base;
using JetBrains.Platform.RdFramework.Impl;
using JetBrains.Platform.RdFramework.Tasks;
using JetBrains.Platform.RdFramework.Util;
using JetBrains.Platform.Unity.EditorPluginModel;
using JetBrains.Rider.Unity.Editor.AssetPostprocessors;
using JetBrains.Util;
using JetBrains.Util.Logging;
using UnityEditor;
using Application = UnityEngine.Application;
using Debug = UnityEngine.Debug;
using JetBrains.Rider.Unity.Editor.NonUnity;
using JetBrains.Rider.Unity.Editor.Utils;
using UnityEditor.Callbacks;

namespace JetBrains.Rider.Unity.Editor
{
  [InitializeOnLoad]
  public static class PluginEntryPoint
  {
    private static readonly IPluginSettings ourPluginSettings;
    private static readonly RiderPathLocator ourRiderPathLocator;
    public static readonly List<ModelWithLifetime> UnityModels = new List<ModelWithLifetime>();
    private static readonly UnityEventCollector ourLogEventCollector;

    // This an entry point
    static PluginEntryPoint()
    {
      ourLogEventCollector = new UnityEventCollector();

      ourPluginSettings = new PluginSettings();
      ourRiderPathLocator = new RiderPathLocator(ourPluginSettings);
      var riderPath = ourRiderPathLocator.GetDefaultRiderApp(EditorPrefsWrapper.ExternalScriptEditor,
        RiderPathLocator.GetAllFoundPaths(ourPluginSettings.OperatingSystemFamilyRider));
      if (string.IsNullOrEmpty(riderPath))
        return;

      AddRiderToRecentlyUsedScriptApp(riderPath);
      if (!PluginSettings.RiderInitializedOnce)
      {
        EditorPrefsWrapper.ExternalScriptEditor = riderPath;
        PluginSettings.RiderInitializedOnce = true;
      }

      if (Enabled)
      {
        Init();
      }
    }

    public delegate void OnModelInitializationHandler(UnityModelAndLifetime e);
    [UsedImplicitly]
    public static event OnModelInitializationHandler OnModelInitialization = delegate {};

    internal static bool CheckConnectedToBackendSync(EditorPluginModel model)
    {
      if (model == null)
        return false;
      var connected = false;
      try
      {
        // HostConnected also means that in Rider and in Unity the same solution is opened
        connected = model.IsBackendConnected.Sync(RdVoid.Instance,
          new RpcTimeouts(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)));
      }
      catch (Exception)
      {
        ourLogger.Verbose("Rider Protocol not connected.");
      }

      return connected;
    }

    public static bool CallRider(string args)
    {
      return ourOpenAssetHandler.CallRider(args);
    }
    
    private static bool ourInitialized;
    
    private static readonly ILog ourLogger = Log.GetLog("RiderPlugin");
    
    internal static string SlnFile;

    public static bool Enabled
    {
      get
      {
        var defaultApp = EditorPrefsWrapper.ExternalScriptEditor;
        return !string.IsNullOrEmpty(defaultApp) && Path.GetFileName(defaultApp).ToLower().Contains("rider");
      }
    }

    private static void Init()
    {
      var projectDirectory = Directory.GetParent(Application.dataPath).FullName;

      var projectName = Path.GetFileName(projectDirectory);
      SlnFile = Path.GetFullPath($"{projectName}.sln");

      InitializeEditorInstanceJson();

      // process csproj files once per Unity process
      if (!RiderScriptableSingleton.Instance.CsprojProcessedOnce)
      {
        ourLogger.Verbose("Call OnGeneratedCSProjectFiles once per Unity process.");
        CsprojAssetPostprocessor.OnGeneratedCSProjectFiles();
        RiderScriptableSingleton.Instance.CsprojProcessedOnce = true;
      }

      Log.DefaultFactory = new RiderLoggerFactory();

      var lifetimeDefinition = Lifetimes.Define(EternalLifetime.Instance);
      var lifetime = lifetimeDefinition.Lifetime;

      AppDomain.CurrentDomain.DomainUnload += (EventHandler) ((_, __) =>
      {
        ourLogger.Verbose("lifetimeDefinition.Terminate");
        lifetimeDefinition.Terminate();
      });

      if (PluginSettings.SelectedLoggingLevel >= LoggingLevel.VERBOSE)
        Debug.Log($"Rider plugin initialized. LoggingLevel: {PluginSettings.SelectedLoggingLevel}. Change it in Unity Preferences -> Rider. Logs path: {LogPath}.");
     
      var list = new List<ProtocolInstance>();
      CreateProtocolAndAdvise(lifetime, list, new DirectoryInfo(Directory.GetCurrentDirectory()).Name);
      
      // list all sln files in CurrentDirectory, except main one and create server protocol for each of them
      var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
      var solutionFiles = currentDir.GetFiles("*.sln", SearchOption.TopDirectoryOnly);
      foreach (var solutionFile in solutionFiles)
      {
        if (Path.GetFileNameWithoutExtension(solutionFile.FullName) != currentDir.Name)
        {
          CreateProtocolAndAdvise(lifetime, list, Path.GetFileNameWithoutExtension(solutionFile.FullName));
        }
      }
      
      ourOpenAssetHandler = new OnOpenAssetHandler(ourRiderPathLocator, ourPluginSettings, SlnFile);
      ourLogger.Verbose("Writing Library/ProtocolInstance.json");
      var protocolInstanceJsonPath = Path.GetFullPath("Library/ProtocolInstance.json");
      File.WriteAllText(protocolInstanceJsonPath, ProtocolInstance.ToJson(list));

      AppDomain.CurrentDomain.DomainUnload += (sender, args) =>
      {
        ourLogger.Verbose("Deleting Library/ProtocolInstance.json");
        File.Delete(protocolInstanceJsonPath);
      };

      ourSavedState = GetEditorState();
      SetupAssemblyReloadEvents();

      ourInitialized = true;
    }

    private static PlayModeState GetEditorState()
    {
      if (EditorApplication.isPaused)
        return PlayModeState.Paused;
      if (EditorApplication.isPlaying)
        return PlayModeState.Playing;
      return PlayModeState.Stopped;
    }

    private static void SetupAssemblyReloadEvents()
    {
#pragma warning disable 618
      EditorApplication.playmodeStateChanged += () =>
#pragma warning restore 618
      {
        var newState = GetEditorState();
        if (ourSavedState != newState)
        {
          if (PluginSettings.AssemblyReloadSettings == AssemblyReloadSettings.RecompileAfterFinishedPlaying)
          {
            if (newState == PlayModeState.Playing)
            {
              EditorApplication.LockReloadAssemblies();
            }
            else if (newState == PlayModeState.Stopped)
            {
              EditorApplication.UnlockReloadAssemblies();
            }
          }  
          ourSavedState = newState;
        }
      };
      
      AppDomain.CurrentDomain.DomainUnload += (sender, args) =>
      {
        if (PluginSettings.AssemblyReloadSettings == AssemblyReloadSettings.StopPlayingAndRecompile)
        {
          if (EditorApplication.isPlaying)
          {
            EditorApplication.isPlaying = false;
          }
        }
      };
    }

    private static void CreateProtocolAndAdvise(Lifetime lifetime, List<ProtocolInstance> list, string solutionFileName)
    {
      try
      {
        var riderProtocolController = new RiderProtocolController(MainThreadDispatcher.Instance, lifetime);
        list.Add(new ProtocolInstance(riderProtocolController.Wire.Port, solutionFileName));

        var serializers = new Serializers();
        var identities = new Identities(IdKind.Server);

        MainThreadDispatcher.AssertThread();

        riderProtocolController.Wire.Connected.WhenTrue(lifetime, connectionLifetime =>
        {
          var protocol = new Protocol("UnityEditorPlugin", serializers, identities, MainThreadDispatcher.Instance, riderProtocolController.Wire);
          ourLogger.Log(LoggingLevel.VERBOSE, "Create UnityModel and advise for new sessions...");
          var model = new EditorPluginModel(connectionLifetime, protocol);
          AdviseUnityActions(model, connectionLifetime);
          AdviseEditorState(model);
          OnModelInitialization(new UnityModelAndLifetime(model, connectionLifetime));
          AdviseRefresh(model);
          InitEditorLogPath(model);

          model.FullPluginPath.Advise(connectionLifetime, AdditionalPluginsInstaller.UpdateSelf);
          model.ApplicationVersion.SetValue(UnityUtils.UnityVersion.ToString());
          model.ScriptingRuntime.SetValue(UnityUtils.ScriptingRuntime);

          ourLogger.Verbose("UnityModel initialized.");
          var pair = new ModelWithLifetime(model, connectionLifetime);
          connectionLifetime.AddAction(() => { UnityModels.Remove(pair); });
          UnityModels.Add(pair);
          new UnityEventLogSender(ourLogEventCollector);
        });
      }
      catch (Exception ex)
      {
        ourLogger.Error("Init Rider Plugin " + ex);
      }
    }

    private static void AdviseEditorState(EditorPluginModel modelValue)
    {
      modelValue.GetUnityEditorState.Set(rdVoid =>
      {
        if (EditorApplication.isPlaying)
        {
          return UnityEditorState.Play;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
          return UnityEditorState.Refresh;
        }
        
        return UnityEditorState.Idle;
      });
    }
    
    private static void AdviseRefresh(EditorPluginModel model)
    {
      model.Refresh.Set((l, force) =>
      {
        var task = new RdTask<RdVoid>();
        MainThreadDispatcher.Instance.Queue(() =>
        {
          if (!EditorApplication.isPlaying && EditorPrefsWrapper.AutoRefresh || force)
            UnityUtils.SyncSolution();
          else
            ourLogger.Verbose("AutoRefresh is disabled via Unity settings.");
          task.Set(RdVoid.Instance);
        });
        return task;
      });
    }

    public enum PlayModeState
    {
      Stopped,
      Playing,
      Paused
    }
    
    private static PlayModeState ourSavedState = PlayModeState.Stopped;
    
    private static void AdviseUnityActions(EditorPluginModel model, Lifetime connectionLifetime)
    {
      var isPlayingAction = new Action(() =>
      {
        MainThreadDispatcher.Instance.Queue(() =>
        {
          var isPlayOrWillChange = EditorApplication.isPlayingOrWillChangePlaymode;
          var isPlaying = isPlayOrWillChange && EditorApplication.isPlaying;
          if (!model.Play.HasValue() || model.Play.HasValue() && model.Play.Value != isPlaying)
            model.Play.SetValue(isPlaying);  
         
          var isPaused = EditorApplication.isPaused;
          model.Pause.SetValue(isPaused);
        });
      });
      isPlayingAction(); // get Unity state
      model.Play.Advise(connectionLifetime, play =>
      {
        MainThreadDispatcher.Instance.Queue(() =>
        {
          var res = EditorApplication.isPlayingOrWillChangePlaymode && EditorApplication.isPlaying;
          if (res != play)
            EditorApplication.isPlaying = play;
        });
      });

      model.Pause.Advise(connectionLifetime, pause =>
      {
        MainThreadDispatcher.Instance.Queue(() =>
        {
          EditorApplication.isPaused = pause;
        });
      });
      
      model.Step.Set((l, x) =>
      {
        var task = new RdTask<RdVoid>();
        MainThreadDispatcher.Instance.Queue(() =>
        {
          EditorApplication.Step();
          task.Set(RdVoid.Instance);
        });
        return task;
      });

      var isPlayingHandler = new EditorApplication.CallbackFunction(() => isPlayingAction());
// left for compatibility with Unity <= 5.5
#pragma warning disable 618
      connectionLifetime.AddBracket(() => { EditorApplication.playmodeStateChanged += isPlayingHandler; },
        () => { EditorApplication.playmodeStateChanged -= isPlayingHandler; });
#pragma warning restore 618
      // new api - not present in Unity 5.5
      // private static Action<PauseState> IsPauseStateChanged(UnityModel model)
      //    {
      //      return state => model?.Pause.SetValue(state == PauseState.Paused);
      //    }
    }

    private static void InitEditorLogPath(EditorPluginModel editorPluginModel)
    {
      // https://docs.unity3d.com/Manual/LogFiles.html
      //PlayerSettings.productName;
      //PlayerSettings.companyName;
      //~/Library/Logs/Unity/Editor.log
      //C:\Users\username\AppData\Local\Unity\Editor\Editor.log
      //~/.config/unity3d/Editor.log

      string editorLogpath = string.Empty;
      string playerLogPath = string.Empty;

      switch (PluginSettings.SystemInfoRiderPlugin.operatingSystemFamily)
      {
        case OperatingSystemFamilyRider.Windows:
        {
          var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
          editorLogpath = Path.Combine(localAppData, @"Unity\Editor\Editor.log");
          var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
          if (!string.IsNullOrEmpty(userProfile))
            playerLogPath = Path.Combine(
              Path.Combine(Path.Combine(Path.Combine(userProfile, @"AppData\LocalLow"), PlayerSettings.companyName),
                PlayerSettings.productName),"output_log.txt");
          break;
        }
        case OperatingSystemFamilyRider.MacOSX:
        {
          var home = Environment.GetEnvironmentVariable("HOME");
          if (!string.IsNullOrEmpty(home))
          {
            editorLogpath = Path.Combine(home, "Library/Logs/Unity/Editor.log");
            playerLogPath = Path.Combine(home, "Library/Logs/Unity/Player.log");
          }
          break;
        }
        case OperatingSystemFamilyRider.Linux:
        {
          var home = Environment.GetEnvironmentVariable("HOME");
          if (!string.IsNullOrEmpty(home))
          {
            editorLogpath = Path.Combine(home, ".config/unity3d/Editor.log");
            playerLogPath = Path.Combine(home, $".config/unity3d/{PlayerSettings.companyName}/{PlayerSettings.productName}/Player.log");
          }
          break;
        }
      }

      editorPluginModel.EditorLogPath.SetValue(editorLogpath);
      editorPluginModel.PlayerLogPath.SetValue(playerLogPath);
    }

    internal static readonly string  LogPath = Path.Combine(Path.Combine(Path.GetTempPath(), "Unity3dRider"), DateTime.Now.ToString("yyyy-MM-ddT-HH-mm-ss") + ".log");
    private static OnOpenAssetHandler ourOpenAssetHandler;

    /// <summary>
    /// Creates and deletes Library/EditorInstance.json containing info about unity instance
    /// </summary>
    private static void InitializeEditorInstanceJson()
    {
      ourLogger.Verbose("Writing Library/EditorInstance.json");

      var editorInstanceJsonPath = Path.GetFullPath("Library/EditorInstance.json");

      File.WriteAllText(editorInstanceJsonPath, $@"{{
  ""process_id"": {Process.GetCurrentProcess().Id},
  ""version"": ""{Application.unityVersion}"",
  ""app_path"": ""{EditorApplication.applicationPath}"",
  ""app_contents_path"": ""{EditorApplication.applicationContentsPath}"",
  ""attach_allowed"": ""{EditorPrefs.GetBool("AllowAttachedDebuggingOfEditor", true)}"",
  ""is_loaded_from_assets"": ""{IsLoadedFromAssets()}""
}}");

      AppDomain.CurrentDomain.DomainUnload += (sender, args) =>
      {
        ourLogger.Verbose("Deleting Library/EditorInstance.json");
        File.Delete(editorInstanceJsonPath);
      };
    }

    private static void AddRiderToRecentlyUsedScriptApp(string userAppPath)
    {
      const string recentAppsKey = "RecentlyUsedScriptApp";
      
      for (var i = 0; i < 10; ++i)
      {
        var path = EditorPrefs.GetString($"{recentAppsKey}{i}");
        // ReSharper disable once PossibleNullReferenceException
        if (File.Exists(path) && Path.GetFileName(path).ToLower().Contains("rider"))
          return;
      }
      
      EditorPrefs.SetString($"{recentAppsKey}{9}", userAppPath);
    }

    /// <summary>
    /// Called when Unity is about to open an asset.
    /// </summary>
    [OnOpenAsset]
    private static bool OnOpenedAsset(int instanceID, int line)
    {
      if (!Enabled) 
        return false;
      if (!ourInitialized)
      {
        // make sure the plugin was initialized first.
        // this can happen in case "Rider" was set as the default scripting app only after this plugin was imported.
        Init();
      }
      
      return ourOpenAssetHandler.OnOpenedAsset(instanceID, line);
    }

    public static bool IsLoadedFromAssets()
    {
      var currentDir = Directory.GetCurrentDirectory();
      var location = Assembly.GetExecutingAssembly().Location;
      return location.StartsWith(currentDir, StringComparison.InvariantCultureIgnoreCase);
    }
  }

  public struct UnityModelAndLifetime
  {
    public EditorPluginModel Model;
    public Lifetime Lifetime;

    public UnityModelAndLifetime(EditorPluginModel model, Lifetime lifetime)
    {
      Model = model;
      Lifetime = lifetime;
    }
  }
}

// Developed with JetBrains Rider =)