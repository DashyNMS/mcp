namespace DashyNMS.Agent.Catalogue;

/// <summary>
/// Every tool's name, title, description and parameter descriptions, as constants, so MCP's
/// <c>[McpServerTool]</c> and <c>[Description]</c> attributes and <see cref="ToolCatalogue"/> read one copy.
/// </summary>
/// <remarks>
/// Attributes take only compile-time constants, which is why the text lives here rather than in
/// <see cref="ToolCatalogue"/>'s records. Hosts that describe tools to a model without MCP (DashyNMS
/// mobile) read <see cref="ToolCatalogue"/> instead of writing their own text, so a model on any host is
/// told the same thing (DashyNMS/mcp#130). Text written for one host's needs, such as a shorter
/// description for a phone, never goes here: this is what MCP lists, and it stays byte for byte as MCP
/// has always listed it.
/// </remarks>
public static class ToolText
{
    /// <summary>How every tool with a time window takes it.</summary>
    public const string WindowHelp =
        "The window is either hours (1 to 168, counting back from until) or since/until. Each of since and until is " +
        "a duration before now (90m, 24h, 7d) or an ISO 8601 time; until defaults to now, and the window defaults to the last 24 hours.";

    /// <summary>The sensor types <c>device_sensors</c> and <c>health_overview</c> accept.</summary>
    public const string TypeHelp =
        "Types are LibreNMS's sensor classes - temperature, fanspeed, dbm (optical power), signal, voltage, current, power, humidity and others - " +
        "and fan, optics and temp work too.";

    /// <summary>What the Unimus config tools hold back.</summary>
    public const string TextHelp =
        "Secrets (passwords, keys, SNMP communities, private keys) are redacted. If the server is set to MCP_CONFIG_TEXT=summary, " +
        "only sizes, times and line counts come back, never config text.";

    /// <summary><c>find_device</c>.</summary>
    public static class FindDevice
    {
        public const string Name = "find_device";
        public const string Title = "Find a device";
        public const string Description =
            "Search LibreNMS for devices by part of a hostname, display name or sysName, by IP address, or by MAC address, " +
            "and/or list devices by status, device group or location. Use it when the exact device isn't known yet, to get device IDs " +
            "for the other tools, or for \"which devices are down at site X?\". A MAC address is looked up in LibreNMS's ARP tables, so it finds the " +
            "routers or layer-3 switches that have seen it (with the IP it had), not the endpoint itself. Best matches first. " +
            "Paged: 10 by default, up to 50; when page.next_offset is set, call again with that offset to get every match rather than " +
            "making narrower searches. Records are short unless verbose is true. It doesn't show health or alerts.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Query =
                "Part of a hostname, display name or sysName, an IP address, or a MAC address (any common format). Can be left out when status, group or location is given.";

            public const string Status =
                "Only devices with this status: up, down or disabled.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).";

            public const string Location =
                "Only devices at this location (by name; list_groups_and_locations shows them).";

            public const string Offset =
                "How many matches to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many matches to return, 1 to 50. Defaults to 10.";

            public const string Verbose =
                "Return every field (sysName, OS, hardware, last polled) instead of the short record.";
        }
    }

    /// <summary><c>network_overview</c>.</summary>
    public static class NetworkOverview
    {
        public const string Name = "network_overview";
        public const string Title = "Network overview";
        public const string Description =
            "The whole network in one call, as the dashboard's counts: how many devices are up, down, disabled or ignored, which locations have the most " +
            "devices down, open alerts by severity and how many are acknowledged, how many ports are down while enabled or counting errors, and the LibreNMS " +
            "version. A cheap first call for \"how's the network?\" before drilling in with find_device, active_alerts or the top_ tools.";
    }

    /// <summary><c>active_alerts</c>.</summary>
    public static class ActiveAlerts
    {
        public const string Name = "active_alerts";
        public const string Title = "Active alerts";
        public const string Description =
            "Alerts in LibreNMS, grouped by device, with rule name, severity and age. With no time window: the alerts open now (firing or " +
            "acknowledged), worst first, for \"what's alerting?\" or \"is anything critical?\". On LibreNMS 26.10 and later one alert is a rule firing on a " +
            "device, and may hold several faults - one per core, port or sensor it matched: fault_count says how many, faults lists them, and totals counts " +
            "alerts and faults separately. alert_id is the first fault's ID, for alert_details; group_id names the device and rule. With a window (hours, or since/until): every alert raised in it, " +
            "including ones that have since recovered, each with still_open and recovered_at - for \"what fired overnight?\" or \"what alerted between " +
            "Friday and Monday?\". " +
            WindowHelp +
            " Filter by severity, device, device group or location. " +
            "Paged: 50 by default, up to 100; use page.next_offset for more. Records are short unless verbose is true.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Severity =
                "Only alerts of this severity: critical, warning or ok. Leave out for all.";

            public const string Device =
                "Only alerts on this device: hostname, display name, IP address or LibreNMS device ID. Leave out for every device.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).";

            public const string Location =
                "Only devices at this location (by name; list_groups_and_locations shows them).";

            public const string Hours =
                "Alerts raised in the last this many hours, 1 to 168. Leave out for alerts open now.";

            public const string Since =
                "Alerts raised since: a duration before now (90m, 24h, 7d) or an ISO 8601 time. Leave out for alerts open now.";

            public const string Until =
                "Alerts raised until: a duration before now or an ISO 8601 time. Defaults to now.";

            public const string Offset =
                "How many alerts to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many alerts to return, 1 to 100. Defaults to 50.";

            public const string Verbose =
                "Include each alert's start time and note.";
        }
    }

    /// <summary><c>device_health</c>.</summary>
    public static class DeviceHealth
    {
        public const string Name = "device_health";
        public const string Title = "Device health";
        public const string Description =
            "How one device is doing now: up or down, uptime, whether it's in a maintenance window, its open alerts, " +
            "peak CPU, memory and fullest storage, and the ports that are down while enabled or are counting errors (top 5). " +
            "Use it for \"is X OK?\" questions. It doesn't cover history; use device_events or what_changed for that, and port_status for one port in detail.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";
        }
    }

    /// <summary><c>device_sensors</c>.</summary>
    public static class DeviceSensors
    {
        public const string Name = "device_sensors";
        public const string Title = "Device sensors";
        public const string Description =
            "One device's sensor readings: temperatures, fan speeds, optical power in dBm, signal, voltages, currents, power and more. Each reading has its " +
            "unit, its limits and a state (ok, warning, critical, or unknown when there's nothing to judge it by), judged the way DashyNMS's Health tab judges " +
            "it: the sensor's own LibreNMS limits where it has them, sensible defaults for dBm, signal, temperature and fans where it doesn't. Problems first. " +
            TypeHelp +
            " For the same across the whole network, use health_overview.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Type =
                "Only this type of sensor, e.g. temperature, fanspeed, dbm or voltage. Leave out for all.";
        }
    }

    /// <summary><c>list_groups_and_locations</c>.</summary>
    public static class ListGroupsAndLocations
    {
        public const string Name = "list_groups_and_locations";
        public const string Title = "Device groups and locations";
        public const string Description =
            "The device groups defined in LibreNMS and the locations devices are at, with how many devices are at each location and how many of those are down. " +
            "Use it to find the names the group and location filters of find_device and active_alerts accept, or for \"which sites are there?\". " +
            "To list a group's devices, call find_device with that group.";
    }

    /// <summary><c>alert_details</c>.</summary>
    public static class AlertDetails
    {
        public const string Name = "alert_details";
        public const string Title = "Alert details";
        public const string Description =
            "Everything about one alert: why it fired (the ports, sensors or other things the rule matched, with the values that tripped it), the rule's " +
            "name, severity, condition, procedure link and notes, its state, acknowledgement note and how long it's been open, and how often the same rule " +
            "has fired on the same device in the last 30 days, with the most recent times - to tell a one-off from a flapping alert. " +
            "Takes an alert ID, which active_alerts gives for open alerts. On LibreNMS 26.10 and later that's a fault ID: the details are that fault's own " +
            "(never another core's or port's), and group lists every fault the same rule has open on the device.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string AlertId =
                "The LibreNMS alert ID - on 26.10 and later, a fault ID - from active_alerts (alert_id, or a fault_id from its faults).";
        }
    }

    /// <summary><c>alert_rules</c>.</summary>
    public static class AlertRules
    {
        public const string Name = "alert_rules";
        public const string Title = "Alert rules";
        public const string Description =
            "LibreNMS's alert rules: each one's name, severity, whether it's enabled, its condition, which devices, device groups and locations it applies to " +
            "(or all), its alert template, how many alerts it has open now and how many it raised in the last 7 days. For \"what are we alerting on?\", " +
            "\"why didn't X alert?\" or \"which rules are noisiest?\". Filter by part of the name or by severity; sort by name, open or raised. " +
            "On LibreNMS 26.10 and later each rule also says whether it notifies per entity (notify_per_entity, max_entities - null max_entities " +
            "inherits the server's default, 20 unless changed), which decides whether acknowledging one fault acknowledges the device's others, and " +
            "flags conditions using device columns 26.10 removed (removed_columns; latency is now device_stats.ping_rtt_last). " +
            "Paged: 50 by default, up to 100. Read-only: rules can only be changed in LibreNMS.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Name =
                "Only rules whose name contains this.";

            public const string Severity =
                "Only rules of this severity: critical, warning or ok.";

            public const string IncludeDisabled =
                "Include disabled rules (by default only enabled ones are listed).";

            public const string Sort =
                "Sort by name (the default), open (most open alerts first) or raised (most raised in the last 7 days first).";

            public const string Offset =
                "How many rules to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many rules to return, 1 to 100. Defaults to 50.";

            public const string Verbose =
                "Include each rule's notes and procedure link.";
        }
    }

    /// <summary><c>port_status</c>.</summary>
    public static class PortStatus
    {
        public const string Name = "port_status";
        public const string Title = "Port status";
        public const string Description =
            "One port on one device: admin and operational state, speed, duplex, VLAN, current traffic and error rates, its description (alias), " +
            "and the neighbour LibreNMS sees on it (LLDP/CDP). Use it for \"is port X up?\" or \"what's plugged into X?\". " +
            "Rates are LibreNMS's last polling interval, not history. For every problem port on a device at once, use device_health.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Port =
                "The port: its name (ifName, e.g. 1/1/7 or Gi0/1), description (ifDescr), alias, or LibreNMS port ID.";
        }
    }

    /// <summary><c>device_neighbours</c>.</summary>
    public static class DeviceNeighbours
    {
        public const string Name = "device_neighbours";
        public const string Title = "Device neighbours";
        public const string Description =
            "Every LLDP and CDP neighbour of one device: the local port, the device on the other end (with its LibreNMS ID when LibreNMS knows it), " +
            "its port, platform and protocol. Includes neighbours that report this device but that it doesn't report itself (seen_from: neighbour), " +
            "which often means a discovery problem on one side. Use it for \"what's connected to this switch?\" or topology questions. " +
            "For one port, port_status shows its neighbour too.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";
        }
    }

    /// <summary><c>trace_endpoint</c>.</summary>
    public static class TraceEndpoint
    {
        public const string Name = "trace_endpoint";
        public const string Title = "Trace endpoint";
        public const string Description =
            "Finds where a MAC or IPv4 address is plugged in: the switch and edge port, with its alias, VLAN and state. " +
            "An IP is turned into its MAC through LibreNMS's ARP tables, then every switch whose forwarding table has learned the MAC is checked, " +
            "and ports that are links to other switches (LLDP/CDP neighbours, aggregates, ports with many MACs) are passed over. " +
            "Also gives every port the MAC was seen on, with its role, and the path of switches from the edge upwards. " +
            "Says when the answer is ambiguous - several possible edge ports, or only uplinks - rather than guessing. " +
            "Only as current as LibreNMS's last FDB and ARP poll.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Address =
                "A MAC address (any common form: aa:bb:cc:dd:ee:ff, aabb.ccdd.eeff, AA-BB-...) or an IPv4 address.";
        }
    }

    /// <summary><c>device_details</c>.</summary>
    public static class DeviceDetails
    {
        public const string Name = "device_details";
        public const string Title = "Device details";
        public const string Description =
            "One device's details beyond its health, in sections - ask only for the ones the question needs: " +
            "inventory (chassis, modules, transceivers, power supplies, fans with models and serials); vlans (each VLAN with its untagged and tagged ports); " +
            "fdb (MAC addresses learned per port - \"what's connected to port X?\"); arp (IP to MAC entries); poe (PoE power budget used and left); " +
            "wireless (access points, clients, channel and signal sensors); routing (BGP sessions, OSPF neighbours, VRFs); " +
            "availability (uptime percentage over the last day, week, month and year); addresses (the device's own IP addresses and which port each is on). " +
            "Without sections, everything except fdb and arp. Lists are paged together with offset and limit (50 rows by default, up to 200); " +
            "a section that couldn't be read is listed under unavailable. For health, use device_health; for sensors, device_sensors; for neighbours, device_neighbours.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Sections =
                "Which sections to return: any of inventory, vlans, fdb, arp, poe, wireless, routing, availability, addresses. Leave out for all but fdb and arp.";

            public const string Offset =
                "How many rows to skip in each list, for paging. Start at 0, then use the next_offset a section's page gives.";

            public const string Limit =
                "How many rows to return per list, 1 to 200. Defaults to 50.";
        }
    }

    /// <summary><c>top_interfaces</c>.</summary>
    public static class TopInterfaces
    {
        public const string Name = "top_interfaces";
        public const string Title = "Top interfaces";
        public const string Description =
            "The busiest ports across the network right now, by LibreNMS's last polling interval: traffic in and out in Mbps and as a share of the port's speed. " +
            "Rank by total, in or out. Filter by device group or location. As desktop's Top interfaces widget. For traffic history, use the device's graphs.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string By =
                "Rank by total (the default), in or out.";

            public const string Limit =
                "How many ports, 1 to 50. Defaults to 10.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name).";

            public const string Location =
                "Only devices at this location (by name).";
        }
    }

    /// <summary><c>top_errors</c>.</summary>
    public static class TopErrors
    {
        public const string Name = "top_errors";
        public const string Title = "Top errors";
        public const string Description =
            "The ports counting the most errors across the network right now, in errors per second in and out, by LibreNMS's last polling interval. " +
            "An empty list means no port is counting errors. Rank by total, in or out; filter by device group or location. As desktop's Top errors widget.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string By =
                "Rank by total (the default), in or out.";

            public const string Limit =
                "How many ports, 1 to 50. Defaults to 10.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name).";

            public const string Location =
                "Only devices at this location (by name).";
        }
    }

    /// <summary><c>top_devices</c>.</summary>
    public static class TopDevices
    {
        public const string Name = "top_devices";
        public const string Title = "Top devices";
        public const string Description =
            "The devices that stand out across the network: by traffic (the default, all their ports' traffic summed, as desktop's Top devices widget) or by " +
            "problems (most ports down while enabled or counting errors). Filter by device group or location.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string By =
                "Rank by traffic (the default), in, out, or problems.";

            public const string Limit =
                "How many devices, 1 to 50. Defaults to 10.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name).";

            public const string Location =
                "Only devices at this location (by name).";
        }
    }

    /// <summary><c>health_overview</c>.</summary>
    public static class HealthOverview
    {
        public const string Name = "health_overview";
        public const string Title = "Health overview";
        public const string Description =
            "Sensor readings across the whole network, worst first, for questions like \"is anything running hot?\", \"which optics are low?\" or " +
            "\"any fans failing?\". By default only readings in warning or critical; set only_problems to false for every reading of a type. " +
            "Judged the same way as device_sensors. " +
            TypeHelp +
            " Filter by device group or location. Paged: 50 by default, up to 100.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Type =
                "Only this type of sensor, e.g. temperature, fanspeed, dbm or voltage. Leave out for every type.";

            public const string OnlyProblems =
                "Only readings in warning or critical (the default). False lists every reading, which needs a type.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).";

            public const string Location =
                "Only devices at this location (by name; list_groups_and_locations shows them).";

            public const string Offset =
                "How many readings to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many readings to return, 1 to 100. Defaults to 50.";
        }
    }

    /// <summary><c>device_events</c>.</summary>
    public static class DeviceEvents
    {
        public const string Name = "device_events";
        public const string Title = "Device events";
        public const string Description =
            "A device's history from LibreNMS for a window of time, newest first: event log entries (interface changes, reboots, discovery), " +
            "alert state changes, and outages with their length. Use it for \"what happened to X?\", \"when did X go down?\" or " +
            "\"what happened between 2 and 4 last night?\". " +
            WindowHelp +
            " " +
            "Paged: 50 entries by default; pass the next_offset from page for more. If LibreNMS's logs don't reach back to the window's start, " +
            "window_complete is false and covered_since says how far they did reach. Config changes and syslog aren't included; what_changed adds those.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Hours =
                "How many hours to look back from until, 1 to 168. Leave out when using since.";

            public const string Since =
                "Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.";

            public const string Until =
                "End of the window: a duration before now or an ISO 8601 time. Defaults to now.";

            public const string Offset =
                "How many entries to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many entries to return, 1 to 100. Defaults to 50.";
        }
    }

    /// <summary><c>network_events</c>.</summary>
    public static class NetworkEvents
    {
        public const string Name = "network_events";
        public const string Title = "Network events";
        public const string Description =
            "The event log and/or alert log for the whole network over a window of time, newest first, as desktop's Logs tab - for \"what happened overnight?\" " +
            "or \"anything rebooted today?\". The event log has interface changes, reboots, discovery and user actions; the alert log has every alert state change. " +
            WindowHelp +
            " Filter by event type, severity, device, device group or location. Paged: 50 by default, up to 100. " +
            "If LibreNMS's logs don't reach back to the window's start, window_complete is false. For one device, device_events also adds its outages.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Log =
                "Which log: events (the default), alerts, or both.";

            public const string Hours =
                "How many hours to look back from until, 1 to 168. Leave out when using since.";

            public const string Since =
                "Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.";

            public const string Until =
                "End of the window: a duration before now or an ISO 8601 time. Defaults to now.";

            public const string Type =
                "Only event-log entries of this type, e.g. interface, reboot, discovery, system. Part of the type is enough.";

            public const string Severity =
                "Only event-log entries of this severity or worse: ok, info, notice, warning or critical.";

            public const string Device =
                "Only this device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name).";

            public const string Location =
                "Only devices at this location (by name).";

            public const string Offset =
                "How many entries to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many entries to return, 1 to 100. Defaults to 50.";
        }
    }

    /// <summary><c>what_changed</c>.</summary>
    public static class WhatChanged
    {
        public const string Name = "what_changed";
        public const string Title = "What changed";
        public const string Description =
            "Everything that changed on a device in a window of time, from every source configured: the LibreNMS timeline (as device_events, up to 30 entries), " +
            "the config diff for the newest Unimus backup that began inside the window, against the one before it (secrets redacted, long diffs cut), " +
            "and how many Graylog messages it sent, with up to 10 of the newest. Use it when something broke and the question is why. " +
            WindowHelp +
            " Graylog is only counted for windows that end now. Each source says when it isn't configured or couldn't be reached.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Hours =
                "How many hours to look back from until, 1 to 168. Leave out when using since.";

            public const string Since =
                "Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.";

            public const string Until =
                "End of the window: a duration before now or an ISO 8601 time. Defaults to now.";
        }
    }

    /// <summary><c>search_logs</c>.</summary>
    public static class SearchLogs
    {
        public const string Name = "search_logs";
        public const string Title = "Search logs";
        public const string Description =
            "Searches Graylog's log messages across the network or for one device, over any window up to 31 days: \"any errors from the core switches overnight?\", " +
            "\"what did this firewall log between 2 and 4am?\". Gives the message count first, then the busiest sources, levels, facilities and applications, " +
            "messages over time, and the newest lines. Narrow it by text in the message, a device (matched on its names and addresses, as LibreNMS's Graylog tab does), " +
            "a maximum syslog level (0 emergency to 7 debug: 3 means errors and worse) and a stream. " +
            "The count and messages over time are exact for any window; the busiest values and the lines come from up to 150 messages, and top_from says which. " +
            "When nothing matches, it says what might be why. For a device's logs alongside its events and config changes, use what_changed.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Text =
                "Words to find in the message text, matched as a phrase. Leave out for every message.";

            public const string Device =
                "A device whose messages to search: hostname, display name, IP address or LibreNMS device ID. Leave out for the whole network.";

            public const string Since =
                "Start of the window: relative (\"90m\", \"6h\", \"3d\") or ISO 8601 (UTC unless a zone is given). Defaults to 24 hours before until.";

            public const string Until =
                "End of the window, the same forms as since. Defaults to now.";

            public const string Hours =
                "Shorthand for since: this many hours before until, 1 to 168. Use since instead for longer windows.";

            public const string Level =
                "Only messages at this syslog level or more severe: 0 emergency, 1 alert, 2 critical, 3 error, 4 warning, 5 notice, 6 informational, 7 debug.";

            public const string Stream =
                "A Graylog stream, by title or ID. Leave out for every stream the account can read.";

            public const string Limit =
                "How many of the newest lines to return, 0 to 50. Defaults to 20; 0 for the counts alone.";
        }
    }

    /// <summary><c>config_backup</c>.</summary>
    public static class ConfigBackup
    {
        public const string Name = "config_backup";
        public const string Title = "Config backup";
        public const string Description =
            "A device's configuration as Unimus backed it up: the newest backup by default, or an earlier one by ID, with the list of recent backups " +
            "(each is a config version: valid_since is when Unimus first saw it, valid_until when it last confirmed it unchanged). " +
            "For \"what's configured on X?\", \"is VLAN 120 on this switch?\" or reading an old version. " +
            TextHelp +
            " " +
            "Long configs are paged by line: 200 lines by default, up to 1000; use lines.next_offset for more. To compare versions, use config_diff.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string BackupId =
                "A backup ID from the backups list. Leave out for the newest.";

            public const string Offset =
                "How many lines to skip, for paging. Start at 0, then use lines.next_offset.";

            public const string Limit =
                "How many lines to return, 1 to 1000. Defaults to 200.";
        }
    }

    /// <summary><c>config_diff</c>.</summary>
    public static class ConfigDiff
    {
        public const string Name = "config_diff";
        public const string Title = "Config diff";
        public const string Description =
            "The difference between two of a device's Unimus config backups, as changed lines only (- removed, + added). By default the newest backup " +
            "against the one before it; give from_backup and to_backup (IDs from config_backup's list) for any two. " +
            TextHelp +
            " " +
            "Long diffs are cut at 200 lines, with the total counted. For changes in a time window together with events and logs, use what_changed.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string FromBackup =
                "The older backup's ID. Leave out (with to_backup) for the one before the newest.";

            public const string ToBackup =
                "The newer backup's ID. Leave out for the newest.";
        }
    }

    /// <summary><c>recent_config_changes</c>.</summary>
    public static class RecentConfigChanges
    {
        public const string Name = "recent_config_changes";
        public const string Title = "Recent config changes";
        public const string Description =
            "Every device whose configuration changed in a window of time, according to Unimus, newest change first - for \"what changed in the network " +
            "since Friday?\" or \"has anyone touched the core switches today?\". " +
            WindowHelp +
            " Filter by device group or location " +
            "(much quicker on a big network: each device is one request to Unimus). Devices Unimus backs up but LibreNMS doesn't know are included when " +
            "there's no filter. This says when each device changed; config_diff on a device shows what changed. Paged: 50 by default, up to 100.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Hours =
                "How many hours to look back from until, 1 to 168. Leave out when using since.";

            public const string Since =
                "Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.";

            public const string Until =
                "End of the window: a duration before now or an ISO 8601 time. Defaults to now.";

            public const string Group =
                "Only devices in this LibreNMS device group (by name).";

            public const string Location =
                "Only devices at this location (by name).";

            public const string Offset =
                "How many changes to skip, for paging. Start at 0, then use page.next_offset.";

            public const string Limit =
                "How many changes to return, 1 to 100. Defaults to 50.";
        }
    }

    /// <summary><c>graph</c>.</summary>
    public static class Graph
    {
        public const string Name = "graph";
        public const string Title = "Graph";
        public const string Description =
            "A LibreNMS graph as an image, shown in the chat: a device's graph (traffic, processor, memory, ping, temperature and the rest of its health graphs) " +
            "or one port's (traffic, packets, errors). Use it when a picture of a trend helps: \"show me the traffic on X this week\". " +
            "Without type, a device gives its traffic graph and the list of graph types it has; a port gives its traffic. " +
            "With sensor, one sensor alone on its own scale (one temperature, one optic's power) rather than every sensor of its kind together; device_sensors lists them with their IDs. " +
            "The time range is range (hour, day, week, month or year; a day by default), or since and until for a custom span up to 31 days. " +
            "The legend lists every line with its figures; turn it off (legend: false) for graphs with many lines, where it would dwarf the graph. " +
            "The image has its own white background with dark text: show it as it is, on a light background, and never recolour it or place it on a dark one, where it can't be read. " +
            "For numbers rather than a picture, use port_status, device_health or device_sensors.";

        /// <summary>Each parameter's description, named after the parameter.</summary>
        public static class Args
        {
            public const string Device =
                "The device: hostname, display name, IP address or LibreNMS device ID.";

            public const string Type =
                "The graph type. For a device: a name from its list (device_bits, device_processor, device_mempool, device_icmp_perf, ...) or traffic, cpu, memory, ping, temperature, storage. For a port: bits (traffic), upkts, nupkts or errors. Leave out for traffic.";

            public const string Port =
                "A port on the device, for that port's graph: its name (ifName), description, alias or port ID. Leave out for a device graph.";

            public const string Sensor =
                "One sensor on the device, for that sensor's own graph: its sensor_id from device_sensors, or its description (e.g. \"Chassis Temperature\"). Type, if given, narrows it to that kind of sensor. Leave out for a device or port graph.";

            public const string Range =
                "How far back: hour, day, week, month or year. Defaults to day. Ignored when since is given.";

            public const string Since =
                "Start of a custom span: relative (\"6h\", \"3d\") or ISO 8601 (UTC unless a zone is given). Up to 31 days before until.";

            public const string Until =
                "End of a custom span, the same forms as since. Defaults to now.";

            public const string Legend =
                "Draw the legend under the graph (each line's current, average and maximum). Defaults to true. " +
                "Turn it off for a graph with many lines - a processor graph with a line per core, a device's traffic across dozens of ports - where the legend would be taller than the graph; " +
                "keep it when the figures matter to the question.";
        }
    }
}
