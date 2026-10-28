# Third-party notices and additional terms

KarSlides is licensed under the MIT License (see `LICENSE`); it covers the KarSlides source code and the KarSlides
components of the binary release. It uses the components below; their licenses apply to them. The additional terms
in sections 1 and 2 apply to everyone who uses or redistributes KarSlides together with NDI® and Microsoft
components, and form part of the license terms of every KarSlides binary release.

## 1. NDI®

NDI® is a registered trademark of Vizrt NDI AB. Learn more about NDI at https://ndi.video/.
KarSlides is not a product of Vizrt NDI AB and is not endorsed or sponsored by Vizrt NDI AB.

KarSlides does **not** include the NDI SDK or the NDI runtime. It loads `Processing.NDI.Lib.x64.dll` of an NDI 6
runtime installed separately by the user (NDI Tools or the NDI runtime installer, https://ndi.video/tools/).

The P/Invoke declarations in `src/KarSlides.Ndi/Interop` mirror the structures and functions of the NDI SDK header
files, which are distributed under the following license:

> Copyright (C) 2023-2026 Vizrt NDI AB. All rights reserved.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation
> the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
> to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of
> the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
> THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
> IN THE SOFTWARE.

**Additional terms for NDI** (required by section 3d of the NDI SDK License Agreement). By using or distributing
KarSlides you agree that:

1. you will not modify the NDI SDK, the NDI runtime, NDI products or any part of them;
2. you will not reverse engineer, disassemble or recompile the NDI SDK, the NDI runtime or NDI products, or any
   protocols they use, whether transmitted over a network or used on the machine they run on, and will not attempt
   to do so;
3. you will not circumvent any technical limitations of the NDI SDK, the NDI runtime or NDI products;
4. you will not remove, obscure or alter any proprietary notices or labels of the NDI SDK, the NDI runtime or NDI
   products;
5. Vizrt NDI AB and its licensors give no warranties of any kind; to the extent permitted by applicable law, they
   are not liable for any damages, whether direct, indirect, incidental or consequential, arising from the use of
   KarSlides;
6. you will comply with all relevant export laws and regulations of the United States regarding KarSlides and the
   NDI components it uses;
7. the NDI SDK and NDI runtime are copyright © Vizrt NDI AB (formerly NDI, Inc.);
8. if you develop products using KarSlides, they must comply with the NDI SDK license, including current and
   complete NDI compatibility, and their license terms must include the terms of this section.

## 2. Microsoft .NET and Windows SDK components

The binary release is self-contained: it includes the .NET 8 runtime, ASP.NET Core and Windows Desktop runtime
(MIT, © .NET Foundation and Contributors, https://github.com/dotnet/runtime, third-party notices at
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), and `Microsoft.Windows.SDK.NET.dll` and
`WinRT.Runtime.dll` from the Windows SDK .NET projection (Microsoft Windows SDK license terms,
https://aka.ms/WinSDKLicenseURL). These Microsoft components are distributed unmodified, only as part of KarSlides
and only to run on Microsoft Windows. You may not alter their copyright, trademark or patent notices; their use is
subject to Microsoft's license terms, which protect them at least as much as described there. Microsoft does not
endorse KarSlides.

## 3. Libraries

| Component | Version | License | Copyright |
|---|---|---|---|
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX | 3.8.3 | MIT | Amer Koleci and contributors |
| Vortice.Mathematics | 2.1.0 | MIT | Amer Koleci and contributors |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | SharpGenTools contributors |
| Serilog | 4.4.0 | Apache-2.0 | Serilog Contributors |
| Serilog.Extensions.Logging | 8.0.0 | Apache-2.0 | Microsoft, Serilog Contributors |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | Serilog Contributors |
| Microsoft.Extensions.* (DependencyInjection, Logging, Options, Primitives) | 8.0.x | MIT | .NET Foundation and Contributors |
| System.Text.Json, System.Text.Encodings.Web, System.IO.Pipelines | 9.0.1 | MIT | .NET Foundation and Contributors |
| Material Symbols (icon paths inlined in `src/KarSlides.Web/common.js`) | — | Apache-2.0 | Google LLC |

Used only to build and test, not distributed: xunit, xunit.runner.visualstudio (Apache-2.0),
Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.TestHost (MIT).

The MIT license text is in `LICENSE` (with the respective copyright holders). The Apache License 2.0 text is in
`licenses/Apache-2.0.txt`.

## 4. Trademarks

Microsoft, PowerPoint and Windows are trademarks of the Microsoft group of companies. vMix is a trademark of
StudioCoast Pty Ltd. KarSlides is not affiliated with or endorsed by Microsoft or StudioCoast.
