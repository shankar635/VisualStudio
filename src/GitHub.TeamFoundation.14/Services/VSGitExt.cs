using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using GitHub.Models;
using GitHub.Services;
using GitHub.Logging;
using GitHub.Extensions;
using GitHub.TeamFoundation.Services;
using Serilog;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.TeamFoundation.Git.Extensibility;
using Task = System.Threading.Tasks.Task;
using static Microsoft.VisualStudio.VSConstants;

namespace GitHub.VisualStudio.Base
{
    /// <summary>
    /// This service acts as an always available version of <see cref="IGitExt"/>.
    /// </summary>
    /// <remarks>
    /// Initialization for this service will be done asynchronously and the <see cref="IGitExt" /> service will be
    /// retrieved using <see cref="GetServiceAsync" />. This means the service can be constructed and subscribed to from a background thread.
    /// </remarks>
    public class VSGitExt : IVSGitExt
    {
        static readonly ILogger log = LogManager.ForContext<VSGitExt>();

        readonly IAsyncServiceProvider asyncServiceProvider;
        readonly ILocalRepositoryModelFactory repositoryFactory;
        readonly object refreshLock = new object();

        IGitExt gitService;
        IReadOnlyList<ILocalRepositoryModel> activeRepositories;

        public VSGitExt(IAsyncServiceProvider asyncServiceProvider)
            : this(asyncServiceProvider, new VSUIContextFactory(), new LocalRepositoryModelFactory(), ThreadHelper.JoinableTaskContext)
        {
        }

        public VSGitExt(IAsyncServiceProvider asyncServiceProvider, IVSUIContextFactory factory, ILocalRepositoryModelFactory repositoryFactory,
            JoinableTaskContext joinableTaskContext)
        {
            JoinableTaskCollection = joinableTaskContext.CreateCollection();
            JoinableTaskCollection.DisplayName = nameof(VSGitExt);
            JoinableTaskFactory = joinableTaskContext.CreateFactory(JoinableTaskCollection);

            this.asyncServiceProvider = asyncServiceProvider;
            this.repositoryFactory = repositoryFactory;

            // Start with empty array until we have a chance to initialize.
            ActiveRepositories = Array.Empty<ILocalRepositoryModel>();

            // Initialize when we enter the context of a Git repository
            var context = factory.GetUIContext(UICONTEXT.RepositoryOpen_guid);
            context.WhenActivated(() => JoinableTaskFactory.RunAsync(InitializeAsync).Task.Forget(log));
        }

        async Task InitializeAsync()
        {
            gitService = await GetServiceAsync<IGitExt>();
            if (gitService == null)
            {
                log.Error("Couldn't find IGitExt service");
                return;
            }

            // Refresh on background thread
            await Task.Run(() => RefreshActiveRepositories());

            gitService.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(gitService.ActiveRepositories))
                {
                    RefreshActiveRepositories();
                }
            };
        }

        public void RefreshActiveRepositories()
        {
            try
            {
                lock (refreshLock)
                {
                    log.Debug(
                        "IGitExt.ActiveRepositories (#{Id}) returned {Repositories}",
                        gitService.GetHashCode(),
                        gitService.ActiveRepositories.Select(x => x.RepositoryPath));

                    ActiveRepositories = gitService?.ActiveRepositories.Select(x => repositoryFactory.Create(x.RepositoryPath)).ToList();
                }
            }
            catch (Exception e)
            {
                log.Error(e, "Error refreshing repositories");
                ActiveRepositories = Array.Empty<ILocalRepositoryModel>();
            }
        }

        public IReadOnlyList<ILocalRepositoryModel> ActiveRepositories
        {
            get
            {
                return activeRepositories;
            }

            private set
            {
                if (value != activeRepositories)
                {
                    log.Debug("ActiveRepositories changed to {Repositories}", value?.Select(x => x.CloneUrl));
                    activeRepositories = value;
                    ActiveRepositoriesChanged?.Invoke();
                }
            }
        }

        public void JoinTillEmpty()
        {
            JoinableTaskFactory.Context.Factory.Run(async () =>
            {
                await JoinableTaskCollection.JoinTillEmptyAsync();
            });
        }

        async Task<T> GetServiceAsync<T>()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            return (T)await asyncServiceProvider.GetServiceAsync(typeof(T));
        }


        public event Action ActiveRepositoriesChanged;

        JoinableTaskCollection JoinableTaskCollection { get; }
        JoinableTaskFactory JoinableTaskFactory { get; }
    }
}