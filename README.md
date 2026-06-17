# SnapStak Mobile

A .NET MAUI engine that reproduces a mobile application as production ready code across 5 native mobile frameworks. The native companion to SnapStak Web Domain. Built on ConteX Law.

## The problem this solves

Rebuilding a mobile interface across native frameworks is slow, manual work, and each rebuild is another chance to drift from the original. SnapStak Mobile removes that. It captures the structure of a mobile application and reproduces it as production ready source code in the native framework you choose. The same input produces the same code on every run.

## What it does

Scan or select a target mobile application. SnapStak Mobile captures its structure and reproduces it as production ready source code for the native framework you pick. The model writes the code. You name the framework.

The five supported native mobile frameworks are: React Native, Flutter, SwiftUI, Jetpack Compose, and .NET MAUI.

## Built on ConteX Law

SnapStak Mobile runs the same ConteX engine as SnapStak Web Domain, shared in `Engine/Shared`. It is not prompt engineering, and it is not a wrapper around a model's best guess. It is built on ConteX Law.

An AI model produces reliable code from a text prompt because code is structured, and structure is something a model can transcribe rather than invent. ConteX Law supplies that same structural completeness through four pillars:

- **Structure**: the shape of the domain, the elements that exist and the form a valid output must take.
- **Behaviour**: the rules that govern that structure, what is permitted, what depends on what, and what follows from what.
- **Influence**: the external authority the output must answer to, the sources and constraints that sit outside the model and outrank it.
- **Objective**: what the output must achieve, the target the other three pillars are held to.

With the four pillars supplied, the transformation is decided at the input rather than left to the model, which is why the output is reproducible and why it does not depend on which model runs underneath. The framework is model agnostic.

ConteX Law is documented in full in three papers on SSRN:

- ConteX Law (foundational paper): https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6609519
- https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6641679
- https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6652458

## How it works

The shared ConteX engine completes the four pillars against the captured structure, and the `MobileConteXPipelineService` drives the transformation through to native framework code. The model calls go to OpenRouter using your own API key, sent directly from the app.

## Repository structure

- `Engine/Shared/` - the shared ConteX engine, the four-pillar models, the structure agent, and the design tool translators
- `Pipeline/` - `MobileConteXPipelineService`, the mobile transformation pipeline
- `Views/` - the app screens, including the scan, transform and settings pages
- `Platforms/Android/` - the Android host, including the local-asset WebView handler
- `Models/` - the scanned and saved application models

## Prerequisites

- .NET 9 SDK with the MAUI workload installed (`dotnet workload install maui`)
- The Android SDK
- Visual Studio 2022 with the .NET Multi-platform App UI development workload
- An Android emulator or a physical Android device (minimum Android API 26)
- Your own OpenRouter API key

## Build and run

Open `SnapStakMobile.sln` in Visual Studio 2022, select an Android emulator or connected device, and run. From the command line:

```
git clone https://github.com/SnapStak-AI/SnapStak-Mobile.git
cd SnapStak-Mobile
dotnet build -t:Run -f net9.0-android
```

Enter your OpenRouter API key in Settings, scan or select a target application, and choose a native framework.

## Reproducibility

The output is deterministic at the input. The same four-pillar context produces the same code on every run, on any capable model rather than depending on one. That is the property ConteX Law exists to give.

## Companion repository

For reproducing websites as web framework code, see SnapStak Web Domain: https://github.com/SnapStak-AI/SnapStak-Web-Domain

## Licence

SnapStak Mobile is released under the GNU Affero General Public License v3.0. You are free to use, study, modify and distribute it, including commercially. If you modify it and either distribute it or run it as a network service, you must release your changes under the same licence. The work stays open.
