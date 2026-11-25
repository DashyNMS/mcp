# Accounts and permissions

## Make a LibreNMS token

1. In LibreNMS, create a user for the assistant, with read-only access.
2. Open the settings menu and choose **API → API Tokens**.
3. Create a token for that user and copy it.

Use a separate user so you can revoke its token without affecting anyone else. The server never changes anything, and a read-only user means LibreNMS would refuse if it tried.

## Choose what it can see

The assistant sees exactly what that user can see. Give it access to all devices, or only to some. When something is refused, the answer says you don't have permission instead of calling it empty.

## See every sensor

LibreNMS gives its network-wide sensor list only to users with the **global read** role or, where roles can be customised, the `sensor.view` permission. Without it, a question across the whole network is answered device by device and says how many devices it covers.

## Unimus and Graylog

Use a read-only account or token in each. See [Add Unimus and Graylog](../how-to/add-unimus-and-graylog.md).
