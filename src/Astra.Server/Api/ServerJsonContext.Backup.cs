using System.Text.Json.Serialization;
using Astra.Server.Backup;

namespace Astra.Server;

// Registrations for the backup and sync settings documents (Astra.Server/Backup), in their own partial file.
[JsonSerializable(typeof(AutoBackupSettings))]
[JsonSerializable(typeof(SyncConfig))]
[JsonSerializable(typeof(SyncState))]
internal partial class ServerJsonContext
{
}
