## Overview
This package provides networking for Foundry using the Fusion networking framework. Users can take advantage of both 
Foundry's networking features and Fusion's networking features at the same time if they wish.

## Installation instructions
Install [Foundry Core](https://foundryxr.github.io/FoundryCore/) first, once you've done that you can install this 
package from the package manager.

To enable this network provider open the foundry config window from the menu bar at `Foundry -> Config Manager` and set 
the network provider to `Photon Fusion + Voice`. The window may set this automatically for you if this is the only 
provider installed, but we still need to at least open the window for the setting to be applied.

## Requirements
The setup wizard will automatically prompt you to install needed dependencies, and provide links to relevant docs, but
here are some direct links for completeness sake.

- [Photon Fusion](https://dashboard.photonengine.com/download/fusion/photon-fusion-1.1.8-f-725.unitypackage) (v1.1.8 stable) (Handles the networking backend)
- [Photon Voice](https://assetstore.unity.com/packages/tools/audio/photon-voice-2-130518) (v2.53) (Handles voice chat through fusion)

## Limitations
Due to the abstracted nature of Foundry's networking system, some features of Fusion may not exposed directly. If you 
do need something exposed you find you can't directly access let us know and we'll see if we can find a solution or add
it to the package.

## Advanced topics 

### Mixing NetworkBehaviours and Foundry NetworkComponents
The networking system is built so that you can use both NetworkBehaviours and Foundry NetworkComponents at the same time
if you wish. If you're only using foundry components than you don't need a Fusion NetworkObject script added to your object
if you don't want to, but if you want to use both you'll need to add one. When we bind a prefab or scene object at runtime 
it checks if a Fusion NetworkObject exists or not and will use an existing Object and scripts if they exist, otherwise 
it will add them for you.
