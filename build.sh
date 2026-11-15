#!/bin/bash
# Builds BZMultiplayer.dll against the game's own assemblies with the Mono C# compiler.
set -e
cd "$(dirname "$0")"
LIBS=/root/build/libs
OUT=out
mkdir -p $OUT
VERSION=$(grep -oP 'PluginVersion = "\K[^"]+' src/Plugin.cs)
echo "Building BZMultiplayer $VERSION"
mcs -target:library -out:$OUT/BZMultiplayer.dll -optimize+ -debug- -nostdlib -noconfig \
  -langversion:7.2 -unsafe- -nowarn:0169,0414,0649,0108,0618 \
  -r:$LIBS/mscorlib.dll -r:$LIBS/System.dll -r:$LIBS/System.Core.dll -r:$LIBS/netstandard.dll \
  -r:$LIBS/UnityEngine.dll -r:$LIBS/UnityEngine.CoreModule.dll -r:$LIBS/UnityEngine.PhysicsModule.dll \
  -r:$LIBS/UnityEngine.AnimationModule.dll -r:$LIBS/UnityEngine.IMGUIModule.dll -r:$LIBS/UnityEngine.InputLegacyModule.dll \
  -r:$LIBS/UnityEngine.TextRenderingModule.dll -r:$LIBS/UnityEngine.AudioModule.dll -r:$LIBS/UnityEngine.UI.dll -r:$LIBS/UnityEngine.UIModule.dll \
  -r:$LIBS/Assembly-CSharp.dll -r:$LIBS/Assembly-CSharp-firstpass.dll -r:$LIBS/Unity.Addressables.dll \
  -r:$LIBS/com.rlabrecque.steamworks.net.dll \
  -r:$LIBS/BepInEx.dll -r:$LIBS/0Harmony.dll -r:$LIBS/Unity.TextMeshPro.dll -r:$LIBS/UnityEngine.ParticleSystemModule.dll \
  -recurse:'src/*.cs'
ls -la $OUT/BZMultiplayer.dll
