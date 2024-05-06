using System.Collections.Generic;
using Fusion;
using System;
using CyberHub.Brane.Setup;
using Fusion.Editor;

namespace Foundry.Fusion.Setup
{

    public class FusionSuggestedConfigurationChecker: IModuleSetupTasks
    {
        private List<string> missingAssemblies = new();
        private double suggestedNetTimeout = 40.0;
        private NetworkProjectConfig config;

        private static bool IsAssemblyDefined(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == name)
                    return true;
            }
            return false;
        }

        public FusionSuggestedConfigurationChecker()
        {
            config = NetworkProjectConfig.Global;
            var suggestions = new[]
            {
                "Foundry.Networking.Fusion",
                "PhotonVoice.Fusion"
            };

            
            var listedAssemblies = new HashSet<string>(config.AssembliesToWeave);
            foreach (var asm in suggestions)
            {
                if (!IsAssemblyDefined(asm))
                    continue;
                if (!listedAssemblies.Contains(asm))
                    missingAssemblies.Add(asm);
            }
        }

        public IModuleSetupTasks.State GetTaskState()
        {
            if (missingAssemblies.Count > 0)
                return IModuleSetupTasks.State.UncompletedRequiredTasks;
            if (config.Network.ConnectionTimeout < suggestedNetTimeout)
                return IModuleSetupTasks.State.UncompletedOptionalTasks;
            return IModuleSetupTasks.State.Completed;
        }

        public List<SetupTaskList> GetTasks()
        {
            
            var configSuggestionList = new SetupTaskList("Settings");
            if (missingAssemblies.Count > 0)
            {
                var addAssemblies = new SetupTask();
                addAssemblies.name = "Fusion Weaver Missing Assemblies";
                addAssemblies.SetTextDescription("Foundry requires that some assemblies be added to the Fusion weaver:\n" + string.Join("\n", missingAssemblies));

                addAssemblies.action = new SetupAction
                {
                    name = "Add Missing Assemblies",
                    callback = () =>
                    {
                        List<string> newAssemblies = new(config.AssembliesToWeave);
                        newAssemblies.AddRange(missingAssemblies);
                        config.AssembliesToWeave = newAssemblies.ToArray();
                        NetworkProjectConfigUtilities.SaveGlobalConfig();
                    }
                };
                
                configSuggestionList.Add(addAssemblies);
            }

            if (config.Network.ConnectionTimeout < suggestedNetTimeout)
            {
                var setTimeout = new SetupTask();
                setTimeout.name = "Increase Fusion Timeout";
                setTimeout.SetTextDescription(
                    "We suggest the Fusion network timeout be increased to at least 40 seconds.");
                setTimeout.urgency = SetupTask.Urgency.Suggested;
                setTimeout.action = new SetupAction
                {
                    name = "Set Timeout",
                    callback = () =>
                    {
                        config.Network.ConnectionTimeout = suggestedNetTimeout;
                        NetworkProjectConfigUtilities.SaveGlobalConfig();
                    }
                };
                configSuggestionList.Add(setTimeout);
            }

            return new List<SetupTaskList> { configSuggestionList };
        }

        public string ModuleName()
        {
            return "Fusion Networking for Foundry";
        }

        public string ModuleSource()
        {
            return "com.cyberhub.foundry.networking.fusion";
        }
    }
}

