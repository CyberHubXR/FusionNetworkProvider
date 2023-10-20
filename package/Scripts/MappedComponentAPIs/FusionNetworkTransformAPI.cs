using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Foundry.Networking
{
    public class FusionNetworkTransformAPI : NetworkTransformAPI
    {
        public Fusion.NetworkTransform netTransform;

        public override void Teleport(Vector3 position, Quaternion rotation)
        {
            netTransform.TeleportToPositionRotation(position, rotation);
        }

        public override void OnConnected(NetworkTransform ft)
        {
            var props = ft.props.GetProps<FusionNetTransformProps>("Fusion");
            if (props.noInterpolationWhenOwned)
            {
                netTransform.InterpolationDataSource = ft.IsOwner ? Fusion.NetworkBehaviour.InterpolationDataSources.NoInterpolation : props.interpolationDataSource;
                ft.Object.OnIDChanged.AddListener((obj, player) =>
                {
                    netTransform.InterpolationDataSource = player.Owner == ft.Object.NetworkProvider.LocalPlayerId ? Fusion.NetworkBehaviour.InterpolationDataSources.NoInterpolation : props.interpolationDataSource;
                });
            }
        }
    }
}
