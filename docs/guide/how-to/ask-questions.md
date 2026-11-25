# Ask good questions

Ask in your own words. The assistant picks the tools. Name the device, site, port or time you mean, and you'll get a quicker and more exact answer.

| You want to know | Try asking |
| --- | --- |
| What needs attention | "What's alerting right now?" or "What fired overnight?" |
| Whether something is all right | "How is core-sw1 doing?" |
| Why an alert fired | "Why is the uplink alert open, and how often has it happened this month?" |
| What changed | "What changed on edge-fw1 between 2 and 4 last night?" |
| Where something is plugged in | "Which port is 192.0.2.117 on?" |
| What's busy or failing | "Which ports are counting the most errors?" |
| What's running hot | "Are any sensors near their limits at the north site?" |
| A trend | "Show me core-sw1's traffic for the last week." |
| What the logs say | "Any errors from the access switches since Friday?" (with Graylog) |
| What a config says | "What changed in edge-fw1's config this week?" (with Unimus) |
| How to do something in DashyNMS | "How do I pin a sensor to my dashboard in Desktop?" Name Desktop, Mobile or the MCP server. |

You can name a device by hostname, display name, address or LibreNMS ID. If more than one matches, the assistant is told which and asks you.

## Read the answer

- **Part is missing.** When something couldn't be read, or a window was too busy to read in full, the result says which part. Ask about a shorter time, one device or one kind of event.
- **A sensor answer covers only some devices.** Without [global read](../reference/permissions.md#see-every-sensor), devices are read one by one for about 20 seconds. Ask again within a few minutes to carry on.
- **It's marked stale.** LibreNMS couldn't be reached, so this is the last good answer, with its time. See [old answers](control-access.md#decide-what-happens-when-librenms-cant-be-reached).
- **Nothing was found.** Check with the same account in LibreNMS. The assistant sees only what that account can see.

Answers can be wrong. Check LibreNMS before acting on one.
