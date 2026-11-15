"""CI-only regression: run an unchanged pre-move consumer against the current binaries.

A source build against Shared cannot detect a missing Agent type forward. Compile
against the actual historical Agent, then replace its assemblies without rebuilding
the consumer. The baseline precedes DeviceResolver's move into Shared.
"""
import io
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
BASELINE = '3e8dda9576719311900c02bc971dbec943d3e51b'

with tempfile.TemporaryDirectory() as directory:
    work = Path(directory)
    old = work / 'baseline'
    old.mkdir()
    archive = subprocess.check_output(['git', 'archive', '--format=tar', BASELINE], cwd=ROOT)
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        source.extractall(old, filter='data')

    consumer = old / 'compat-consumer'
    consumer.mkdir()
    (consumer / 'Consumer.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include="../src/DashyNMS.Agent/DashyNMS.Agent.csproj" /></ItemGroup>
</Project>
''')
    (consumer / 'Program.cs').write_text('''using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Models;

var resolver = new DeviceResolver(new DeviceDirectory(null!, TimeProvider.System));
var candidate = DeviceResolver.Candidate.From(new Device { DeviceId = 7, Hostname = "example.net" });
if (candidate.DeviceId != 7 || candidate.Hostname != "example.net")
    throw new Exception("The original Candidate API changed.");
if (resolver.GetType().Assembly.GetName().Name != "DashyNMS.Agent.Shared" ||
    candidate.GetType().Assembly.GetName().Name != "DashyNMS.Agent.Shared")
    throw new Exception("The old Agent type references did not resolve to Shared.");
Console.WriteLine("Pre-move DeviceResolver and nested Candidate resolve through Agent forwards.");
''')
    subprocess.run(['dotnet', 'build', str(consumer / 'Consumer.csproj'), '-c', 'Release', '-warnaserror'],
                   cwd=old, check=True)
    output = consumer / 'bin/Release/net10.0'
    for assembly in ('DashyNMS.Agent', 'DashyNMS.Agent.Shared'):
        current = ROOT / 'src' / assembly / 'bin/Release/net10.0' / f'{assembly}.dll'
        if not current.is_file():
            raise SystemExit(f'Build the current solution first: missing {current}')
        shutil.copy2(current, output / current.name)
    # Deliberately execute the DLL: dotnet run would rebuild against the old projects.
    subprocess.run(['dotnet', str(output / 'Consumer.dll')], cwd=output, check=True)
