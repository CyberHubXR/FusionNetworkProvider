using System.Collections;
using System.Collections.Generic;
using Foundry.Core.Editor;
using UnityEditor;
using UnityEngine;

namespace Foundry.Core.Setup
{
    public class FusionDependencyInstaller: IModuleSetupTasks
    {
        private bool isFusionInstalled;
        private bool isVoiceInstalled;
        
        public FusionDependencyInstaller()
        {
            isFusionInstalled = PackageManagerUtil.IsAssemblyDefined("Fusion.Common");
            isVoiceInstalled = PackageManagerUtil.IsAssemblyDefined("PhotonVoice.Fusion");
        }
        
        public IModuleSetupTasks.State GetTaskState()
        {
            return isFusionInstalled && isVoiceInstalled ? IModuleSetupTasks.State.Completed : IModuleSetupTasks.State.UncompletedRequiredTasks;
        }

        public List<SetupTaskList> GetTasks()
        {
            var tasks = new SetupTaskList("Dependencies");
            if (!PackageManagerUtil.IsAssemblyDefined("Fusion.Common"))
            {
                var addFusionTask = new SetupTask();
                addFusionTask.name = "Photon Fusion";
                addFusionTask.SetTextDescription("foundry.core requires the Photon Fusion SDK, refer to the Photon docs for the installation process.");
                addFusionTask.action = SetupAction.OpenDocLink("https://doc.photonengine.com/fusion/v1/getting-started/sdk-download");
                addFusionTask.disableAfterAction = false;
                tasks.Add(addFusionTask);
            }

            if (!PackageManagerUtil.IsAssemblyDefined("PhotonVoice.Fusion"))
            {
                var addVoiceTask = new SetupTask();
                addVoiceTask.name = "Photon Voice";
                addVoiceTask.SetTextDescription("foundry.core requires the Photon Voice SDK, clicking the install button will take you the official Photon docs that explain the installation process.");
                addVoiceTask.action = SetupAction.OpenDocLink("https://doc.photonengine.com/voice/current/getting-started/voice-for-fusion#import_photon_voice");
                addVoiceTask.disableAfterAction = false;
                tasks.Add(addVoiceTask);
            }

            return new List<SetupTaskList> { tasks };
        }

        public string ModuleName()
        {
            return "Fusion networking for Foundry";
        }

        public string ModuleSource()
        {
            return "com.cyberhub.foundry.networking.fusion";
        }
    }
}
