using System.Runtime.CompilerServices;

// Keep the existing Agent type identities resolvable after moving their implementations.
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.AgentOptions))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.ConfigText))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.PageInfo))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Paging))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.ToolException))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Redaction.Redactor))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Summaries.DeviceSummary))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Summaries.DeviceBrief))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.DeviceDirectory))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.DeviceScope))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.Scope))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.DeviceSearch))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.FindDevicesRequest))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.ArpSighting))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.DeviceMatch))]
[assembly: TypeForwardedTo(typeof(DashyNMS.Agent.Devices.FindDevicesResult))]
