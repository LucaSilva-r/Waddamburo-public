"""Packs this folder as a .vsix (a zip with VS Code's manifest; no npm, no vsce): python3 package-vsix.py [out.vsix]."""
import json
import os
import sys
import zipfile

here = os.path.dirname(os.path.abspath(__file__))
manifest = json.load(open(os.path.join(here, "package.json"), encoding="utf-8"))
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, f"waddamburo-{manifest['version']}.vsix")
files = [f for f in ("package.json", "extension.js", "wd-names.json", "snippets.json", "language-configuration.json",
                     "README.md", "icon.png", "syntaxes/actionscript2.tmLanguage.json")]
content_types = """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension=".json" ContentType="application/json"/><Default Extension=".js" ContentType="application/javascript"/>
<Default Extension=".md" ContentType="text/markdown"/><Default Extension=".png" ContentType="image/png"/><Default Extension=".vsixmanifest" ContentType="text/xml"/>
</Types>"""
vsixmanifest = f"""<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="{manifest['name']}" Version="{manifest['version']}" Publisher="{manifest['publisher']}"/>
    <DisplayName>{manifest['displayName']}</DisplayName>
    <Description xml:space="preserve">{manifest['description']}</Description>
    <Categories>Programming Languages</Categories>
    <Icon>extension/icon.png</Icon>
    <Properties><Property Id="Microsoft.VisualStudio.Code.Engine" Value="{manifest['engines']['vscode']}"/></Properties>
  </Metadata>
  <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code"/></Installation>
  <Dependencies/>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/>
    <Asset Type="Microsoft.VisualStudio.Services.Icons.Default" Path="extension/icon.png" Addressable="true"/>
  </Assets>
</PackageManifest>"""
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as vsix:
    vsix.writestr("[Content_Types].xml", content_types)
    vsix.writestr("extension.vsixmanifest", vsixmanifest)
    for name in files:
        vsix.write(os.path.join(here, name), "extension/" + name)
print(out)
