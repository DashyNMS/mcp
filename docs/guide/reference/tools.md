# Tools

The assistant chooses these itself. You need their names to [turn one off](../how-to/control-access.md#turn-tools-off).

## The network

| Tool | What it answers |
| --- | --- |
| `network_overview` | Devices up and down, alerts by severity, ports down or counting errors |
| `active_alerts` | What's alerting, or what was raised in a window of time |
| `network_events` | What happened across the network, and when |
| `health_overview` | Sensors near or past their limits, worst first |
| `top_interfaces`, `top_errors`, `top_devices` | What's busiest or failing |
| `list_groups_and_locations` | Device groups and locations, with how many devices are down |

## Devices and ports

| Tool | What it answers |
| --- | --- |
| `find_device` | Find by name, address or MAC, or list by status, group or location |
| `device_health` | Whether a device is all right |
| `device_details` | Inventory, VLANs, MAC and ARP tables, PoE, wireless, routing and availability |
| `device_sensors` | Temperatures, fans, optical power and the rest |
| `port_status` | A port's state, speed, VLANs, traffic, errors and neighbour |
| `device_neighbours` | What a device is connected to |
| `trace_endpoint` | Which switch port an address or MAC is on |
| `device_events` | A device's events, alerts and outages |
| `graph` | A LibreNMS graph as a picture |

## Alerts and rules

| Tool | What it answers |
| --- | --- |
| `alert_details` | Why an alert fired, and how often it has |
| `alert_rules` | What each rule watches for, and which are noisiest |

## Help with DashyNMS

| Tool | What it answers |
| --- | --- |
| `dashynms_help` | How to do something in DashyNMS Desktop, DashyNMS Mobile or this server, from their user guides |

It doesn't read your network, so it's the one tool that needs no LibreNMS. Each answer says which product and release it describes.

## Changes, logs and configuration

| Tool | What it answers | Needs |
| --- | --- | --- |
| `what_changed` | Events, alerts, config changes and messages around a time | |
| `search_logs` | Log messages | Graylog |
| `config_backup` | A device's configuration backup | Unimus |
| `config_diff` | What changed between two backups | Unimus |
| `recent_config_changes` | Which devices' configuration changed | Unimus |
