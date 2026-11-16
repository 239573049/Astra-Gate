using System.Text.Json.Serialization;

namespace Astra.Api.Contracts;

// Registrations for BackupDtos.cs (its own partial file so no other line of ContractsJsonContext.cs changes).
[JsonSerializable(typeof(BackupInfoDto))]
[JsonSerializable(typeof(BackupExportInput))]
[JsonSerializable(typeof(BackupExportDto))]
[JsonSerializable(typeof(BackupSourceInput))]
[JsonSerializable(typeof(BackupItemDto))]
[JsonSerializable(typeof(BackupSectionPlanDto))]
[JsonSerializable(typeof(BackupPlanDto))]
[JsonSerializable(typeof(BackupRestoreDto))]
[JsonSerializable(typeof(AutoBackupFileDto))]
[JsonSerializable(typeof(AutoBackupDto))]
[JsonSerializable(typeof(AutoBackupInput))]
[JsonSerializable(typeof(SyncTargetDto))]
[JsonSerializable(typeof(SyncConflictDto))]
[JsonSerializable(typeof(SyncStatusDto))]
[JsonSerializable(typeof(SyncTargetInput))]
[JsonSerializable(typeof(SyncConfigInput))]
[JsonSerializable(typeof(SyncRunDto))]
[JsonSerializable(typeof(RemoteBackupInput))]
[JsonSerializable(typeof(RemoteBackupDto))]
public partial class ContractsJsonContext
{
}
