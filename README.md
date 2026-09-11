# 21Emo

Facial expression animator generator for VRChat avatars using Animator As Code (AacV1).  
Animator As Code (AacV1) を使用した、VRChatアバター用表情アニメーター自動生成エディタ拡張です。

## Installation

### Using VCC (VRChat Creator Companion) or ALCOM
1. Open [this link](https://yuleo21.github.io/21tools/)
2. Click "Add to VCC"
3. Click "Open with VCC (or ALCOM)"
4. Add **21Emo** to your avatar project.

## Usage
Menu bar -> `21tools` -> `21Emo`  
(or `Tools` -> `21Emo` -> `表情アニメーター作成`)

## Features
- **Automatic Avatar Import**: Drag and drop an avatar GameObject (`VRCAvatarDescriptor`) to automatically extract facial gesture animation clips from the FX layer and determine Either/Both hand mode.
- **Entry/Exit Transition Architecture**: Generated animator controller uses a clean Entry/Exit pattern with 0.1s transition duration.
- **Both & Either Hand Modes**: Support for separate left/right hand gestures (`Both`) or shared gestures (`Either`).
- **Write Defaults (WD) Control**: Toggle Write Defaults ON/OFF. Automatically locks to ON when Idle is empty to prevent broken avatar faces.
- **FaceFix Options**: Support up to 8 fixed face expressions via the `FaceFix` parameter.
