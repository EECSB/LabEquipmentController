using System;
using System.Collections.Generic;
using System.Linq;

namespace LabEquipmentController
{
    /// <summary>
    /// The user's AI connections and their keys, read and written as one thing.
    ///
    /// The connections live in <see cref="UserSettings"/> as plain JSON; the keys live beside
    /// them encrypted for this Windows account by <see cref="SecretStore"/>, one per connection
    /// id. Keeping both behind one door is what stops a caller saving a connection and forgetting
    /// its key, or deleting one and leaving the key behind in the file.
    ///
    /// Keyed by id rather than by name or position, because a connection can be renamed, pointed
    /// at another provider, or moved up the list, and none of those should lose the key stored
    /// against it.
    /// </summary>
    internal static class AiBookStore
    {
        /// <summary>
        /// Every connection, and the keys that could be decrypted for this account.
        ///
        /// A settings file written before there were several holds one connection and one key;
        /// both are folded in as the first entry, so this change does not cost anyone the
        /// connection they already had. A key that will not decrypt — a profile copied from
        /// another machine — is simply absent, which the caller treats as "no key set" rather
        /// than as an error.
        /// </summary>
        public static (AiConnections Book, Dictionary<string, string> Keys) Load()
        {
            UserSettings settings = SettingsStore.Load();
            AiConnections book = settings.AiBookOrMigrated();

            var keys = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> stored in settings.AiApiKeysProtected ?? [])
            {
                if (SecretStore.Unprotect(stored.Value) is { Length: > 0 } plain)
                    keys[stored.Key] = plain;
            }

            // The one key, from before there were several. It belongs to the connection that the
            // one connection became, which is the first entry.
            if (book.Items.Count > 0 && !keys.ContainsKey(book.Items[0].Id)
                && SecretStore.Unprotect(settings.AiApiKeyProtected) is { Length: > 0 } only)
            {
                keys[book.Items[0].Id] = only;
            }

            return (book, keys);
        }

        /// <summary>
        /// Write both back, encrypting each key and dropping any that belongs to no connection.
        ///
        /// The single-connection fields are cleared as they are replaced. Leaving them would mean
        /// a file holding the same key twice, in two shapes, with nothing to say which is current.
        /// </summary>
        public static void Save(AiConnections book, IReadOnlyDictionary<string, string> keys)
        {
            UserSettings settings = SettingsStore.Load();
            settings.AiBook = book;
            settings.Ai = null;
            settings.AiApiKeyProtected = null;

            var ids = book.Items.Select(c => c.Id).ToHashSet();
            var stored = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> key in keys)
            {
                if (!ids.Contains(key.Key) || key.Value.Length == 0) continue;
                if (SecretStore.Protect(key.Value) is { Length: > 0 } sealedKey)
                    stored[key.Key] = sealedKey;
            }
            settings.AiApiKeysProtected = stored;

            SettingsStore.Save(settings);
        }

        /// <summary>
        /// Use this connection, everywhere, from now on.
        ///
        /// One selection for the app rather than one per window: picking a connection in the
        /// datasheet window is picking the connection, which is the same rule the PDF switch is
        /// held to. Two places that can disagree about what is in force is one place too many.
        /// </summary>
        public static void Select(string id)
        {
            UserSettings settings = SettingsStore.Load();
            AiConnections book = settings.AiBookOrMigrated();
            if (!book.Select(id)) return;

            settings.AiBook = book;
            SettingsStore.Save(settings);
        }

        /// <summary>
        /// How hard to work this connection's model, changed from a window that spends it.
        ///
        /// The same rule as <see cref="Select"/> and for the same reason: effort is a property of
        /// the connection, so setting it in the script writer sets it for the settings box and
        /// the datasheet reader too. A window that kept its own idea of the effort would be a
        /// second place that could disagree with the first.
        ///
        /// Only this field is written back. The connection is re-read here rather than taken from
        /// the caller, so a window holding a clone from before the settings box was last used
        /// cannot undo an endpoint or a model along with it.
        /// </summary>
        public static void SetEffort(string id, AiEffort effort)
        {
            UserSettings settings = SettingsStore.Load();
            AiConnections book = settings.AiBookOrMigrated();
            if (book.Find(id) is not { } connection || connection.Effort == effort) return;

            connection.Effort = effort;
            settings.AiBook = book;
            SettingsStore.Save(settings);
        }
    }
}
