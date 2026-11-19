public static class RunPolicy
{
    public static bool Valid(RunInput input)=>
        input.EventId!=Guid.Empty && input.Status is ("Succeeded" or "Warning" or "Failed" or "Cancelled" or "Skipped") &&
        input.BackupType is ("Full" or "Differential" or "Log") && input.StartedAt!=default && input.FinishedAt>=input.StartedAt &&
        input.FinishedAt-input.StartedAt<=TimeSpan.FromDays(30) && input.SizeBytes>=0 && input.Message is not null && input.Message.Length<=4000 &&
        input.FileName is not null && input.FileName.Length<=240 && !input.FileName.Contains('/') && !input.FileName.Contains('\\') &&
        !input.FileName.Contains("..") && !input.FileName.Any(char.IsControl) &&
        (input.Status!="Succeeded" || !string.IsNullOrWhiteSpace(input.FileName)) &&
        (input.Status!="Skipped" || (input.CommandId is null && input.SizeBytes==0 && input.FileName.Length==0 && !string.IsNullOrWhiteSpace(input.Message)));
}
