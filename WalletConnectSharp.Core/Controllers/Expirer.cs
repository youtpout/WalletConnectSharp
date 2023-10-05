using WalletConnectSharp.Common.Model.Errors;
using WalletConnectSharp.Common.Utils;
using WalletConnectSharp.Core.Interfaces;
using WalletConnectSharp.Core.Models.Expirer;
using WalletConnectSharp.Core.Models.Heartbeat;
using WalletConnectSharp.Events;

namespace WalletConnectSharp.Core.Controllers
{
    /// <summary>
    /// The Expirer module keeps track of expiration dates and triggers an event when an expiration date
    /// has passed
    /// </summary>
    public class Expirer : IExpirer
    {
        /// <summary>
        /// The version of this module
        /// </summary>
        public static readonly string Version = "0.3";
        
        private Dictionary<string, Expiration> _expirations = new Dictionary<string, Expiration>();
        private bool initialized = false;
        private Expiration[] _cached = Array.Empty<Expiration>();
        private ICore _core;

        /// <summary>
        /// The name of this module instance
        /// </summary>
        public string Name
        {
            get
            {
                return $"{_core.Name}-expirer";
            }
        }

        /// <summary>
        /// The context string to use for this module instance
        /// </summary>
        public string Context
        {
            get
            {
                return Name;
            }
        }

        /// <summary>
        /// The <see cref="EventDelegator"/> this module uses to emit events
        /// </summary>
        public EventDelegator Events { get; }

        /// <summary>
        /// The string key value this module will use when storing data in the <see cref="ICore.Storage"/> module
        /// module 
        /// </summary>
        public string StorageKey
        {
            get
            {
                return WalletConnectCore.STORAGE_PREFIX + Version + "//" + Name;
            }
        }

        public event EventHandler<ExpirerEventArgs> Created;
        public event EventHandler<ExpirerEventArgs> Deleted;
        public event EventHandler<ExpirerEventArgs> Expired;
        public event EventHandler Sync;

        /// <summary>
        /// The number of expirations this module is tracking
        /// </summary>
        public int Length
        {
            get
            {
                return _expirations.Count;
            }
        }

        /// <summary>
        /// An array of key values that represents each expiration this module is tracking
        /// </summary>
        public string[] Keys
        {
            get
            {
                return _expirations.Keys.ToArray();
            }
        }

        /// <summary>
        /// An array of expirations this module is tracking
        /// </summary>
        public Expiration[] Values
        {
            get
            {
                return _expirations.Values.ToArray();
            }
        }

        /// <summary>
        /// Create a new Expirer module using the given <see cref="ICore"/> module
        /// </summary>
        /// <param name="core">The <see cref="ICore"/> module the Expirer should reference for Storage</param>
        public Expirer(ICore core)
        {
            this._core = core;
            Events = new EventDelegator(this);
        }

        /// <summary>
        /// Initialize this module. This will restore all stored expiration from Storage
        /// </summary>
        public async Task Init()
        {
            if (!initialized)
            {
#pragma warning disable CS0618 // Old event system
                WrapOldEvents();
#pragma warning restore CS0618 // Old event system
                
                await Restore();

                foreach (var expiration in _cached)
                {
                    _expirations.Add(expiration.Target, expiration);
                }

                _cached = Array.Empty<Expiration>();
                RegisterEventListeners();
                initialized = true;
            }
        }

        [Obsolete("TODO: This needs to be removed in future versions")]
        private void WrapOldEvents()
        {
            this.Created += this.WrapEventHandler<ExpirerEventArgs>(ExpirerEvents.Created);
            this.Deleted += this.WrapEventHandler<ExpirerEventArgs>(ExpirerEvents.Deleted);
            this.Expired += this.WrapEventHandler<ExpirerEventArgs>(ExpirerEvents.Expired);
            this.Sync += this.WrapEventHandler(ExpirerEvents.Sync);
        }

        /// <summary>
        /// Determine whether this Expirer is tracking an expiration with the given string key (usually a topic). 
        /// </summary>
        /// <param name="key">The key of the expiration to check existence for</param>
        /// <returns>True if the given key is being tracked by this module, false otherwise</returns>
        public bool Has(string key)
        {
            try
            {
                var target = FormatTarget("topic", key);
                return GetExpiration(target) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Determine whether this Expirer is tracking an expiration with the given long key (usually an id). 
        /// </summary>
        /// <param name="key">The key of the expiration to check existence for</param>
        /// <returns>True if the given key is being tracked by this module, false otherwise</returns>
        public bool Has(long key)
        {
            try
            {
                var target = FormatTarget("id", key);
                return GetExpiration(target) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Store a new expiration date with the given string key (usually a topic).
        /// This will also start tracking for the expiration date
        /// </summary>
        /// <param name="key">The string key of the expiration to store</param>
        /// <param name="expiry">The expiration date to store</param>
        public void Set(string key, long expiry)
        {
            IsInitialized();
            SetWithTarget("topic", key, expiry);
        }

        /// <summary>
        /// Store a new expiration date with the given long key (usually a id).
        /// This will also start tracking for the expiration date
        /// </summary>
        /// <param name="key">The long key of the expiration to store</param>
        /// <param name="expiry">The expiration date to store</param>
        public void Set(long key, long expiry)
        {
            IsInitialized();
            SetWithTarget("id", key, expiry);
        }

        private void SetWithTarget(string targetType, object key, long expiry)
        {
            var target = FormatTarget(targetType, key);
            var expiration = new Expiration()
            {
                Target = target,
                Expiry = expiry
            };

            if (_expirations.ContainsKey(target))
                _expirations.Remove(target); // We cannot override, so remove first
            
            _expirations.Add(target, expiration);
            CheckExpiry(target, expiration);
            this.Created?.Invoke(this, new ExpirerEventArgs()
            {
                Expiration = expiration,
                Target = target
            });
        }

        /// <summary>
        /// Get an expiration date with the given string key (usually a topic)
        /// </summary>
        /// <param name="key">The string key to get the expiration for</param>
        /// <returns>The expiration date</returns>
        public Expiration Get(string key)
        {
            IsInitialized();
            return GetWithTarget("topic", key);
        }

        /// <summary>
        /// Get an expiration date with the given long key (usually an id)
        /// </summary>
        /// <param name="key">The long key to get the expiration for</param>
        /// <returns>The expiration date</returns>
        public Expiration Get(long key)
        {
            IsInitialized();
            return GetWithTarget("id", key);
        }

        private Expiration GetWithTarget(string targetType, object key)
        {
            var target = FormatTarget(targetType, key);
            return GetExpiration(target);
        }

        /// <summary>
        /// Delete a expiration with the given string key (usually a topic).
        /// </summary>
        /// <param name="key">The string key of the expiration to delete</param>
        public Task Delete(string key)
        {
            IsInitialized();
            DeleteWithTarget("topic", key);

            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Delete a expiration with the given long key (usually a id).
        /// </summary>
        /// <param name="key">The long key of the expiration to delete</param>
        public Task Delete(long key)
        {
            IsInitialized();
            DeleteWithTarget("id", key);

            return Task.CompletedTask;
        }

        private void DeleteWithTarget(string targetType, object key)
        {
            var target = FormatTarget(targetType, key);
            var exists = Has(target);
            if (exists)
            {
                var expiration = GetExpiration(target);
                _expirations.Remove(target);
                this.Deleted?.Invoke(this, new ExpirerEventArgs()
                {
                    Target = target,
                    Expiration = expiration
                });
            }
        }

        private Task SetExpiration(Expiration[] expirations)
        {
            return _core.Storage.SetItem(StorageKey, expirations);
        }

        private async Task<Expiration[]> GetExpirations()
        {
            if (!(await _core.Storage.HasItem(StorageKey)))
                await _core.Storage.SetItem(StorageKey, Array.Empty<Expiration>());
            return await _core.Storage.GetItem<Expiration[]>(StorageKey);
        }

        private async void Persist(object sender, ExpirerEventArgs args)
        {
            await SetExpiration(Values);
            this.Sync?.Invoke(this, EventArgs.Empty);
        }

        private async Task Restore()
        {
            var persisted = await GetExpirations();
            if (persisted == null) return;
            if (persisted.Length == 0) return;
            if (_expirations.Count > 0)
            {
                throw WalletConnectException.FromType(ErrorType.RESTORE_WILL_OVERRIDE, Name);
            }

            _cached = persisted;
        }

        private Expiration GetExpiration(string target)
        {
            if (!_expirations.ContainsKey(target))
                throw WalletConnectException.FromType(ErrorType.NO_MATCHING_KEY, $"{Name}: {target}");

            return _expirations[target];
        }

        private void CheckExpiry(string target, Expiration expiration)
        {
            var expiry = expiration.Expiry;
            var msToTimeout = (expiry * 1000) - DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (msToTimeout <= 0) Expire(target, expiration);
        }

        private void Expire(string target, Expiration expiration)
        {
            _expirations.Remove(target);
            this.Expired?.Invoke(this, new ExpirerEventArgs()
            {
                Target = target,
                Expiration = expiration
            });
        }

        private void CheckExpirations(object sender, EventArgs args)
        {
            var clonedArray = _expirations.Keys.ToArray();
            foreach (var target in clonedArray)
            {
                var expiration = _expirations[target];
                CheckExpiry(target, expiration);
            }
        }

        private void RegisterEventListeners()
        {
            _core.HeartBeat.OnPulse += CheckExpirations;
            //_core.HeartBeat.On(HeartbeatEvents.Pulse, CheckExpirations);

            this.Created += Persist;
            this.Expired += Persist;
            this.Deleted += Persist;
        }

        private void IsInitialized()
        {
            if (!initialized)
            {
                throw WalletConnectException.FromType(ErrorType.NOT_INITIALIZED, Name);
            }
        }

        private string FormatTarget(string targetType, object key)
        {
            if (key is string s && s.StartsWith($"{targetType}:")) return s;

            switch (targetType.ToLower())
            {
                case "topic":
                    if (!(key is string))
                        throw new ArgumentException("Value must be \"string\" for expirer target type: topic");
                    break;
                case "id":
                    if (!key.IsNumericType())
                        throw new ArgumentException("Value must be \"number\" for expirer target type: id");
                    break;
                default:
                    throw new ArgumentException($"Unknown expirer target type: ${targetType}");
            }
            
            return $"{targetType}:{key}";
        }

        public void Dispose()
        {
            Events?.Dispose();
        }
    }
}
