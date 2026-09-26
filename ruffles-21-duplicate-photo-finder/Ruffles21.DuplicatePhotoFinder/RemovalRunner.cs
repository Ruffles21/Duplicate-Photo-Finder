using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed record RemovalResult(IReadOnlyList<Photo> Removed, IReadOnlyList<string> Errors, bool Canceled);

public static class RemovalRunner
{
    public static Task<RemovalResult> RunAsync(IReadOnlyList<Photo> photos, CancellationToken token, Action<string> progress)
    {
        var completion = new TaskCompletionSource<RemovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var removed = new List<Photo>();
            var errors = new List<string>();
            bool canceled = false;
            foreach (var photo in photos)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    SafeRemoval.Remove(photo, token, progress);
                    removed.Add(photo);
                }
                catch (OperationCanceledException) { canceled = true; break; }
                catch (Exception ex) { errors.Add(photo.Path + ": " + ex.Message); }
            }
            completion.SetResult(new RemovalResult(removed, errors, canceled));
        })
        {
            IsBackground = true,
            Name = "Recycle verified exact copies"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
