using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Foundry.Networking
{
    [System.Serializable]
    public class FusionNetTransformProps
    {
        public NetworkBehaviour.InterpolationDataSources interpolationDataSource;
        public bool noInterpolationWhenOwned = true;
        public Fusion.Spaces interpolationSpace;
        public bool isRigidbody;
    }

}
