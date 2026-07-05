// ── Passkey support for AeroDbIdentity ──────────────────────────
// WebAuthn + Alpine.js integration

// ── Global type declarations ────────────────────────────────────

declare var Alpine: {
  data(name: string, component: () => object): void;
};

// ── Types ───────────────────────────────────────────────────────

interface PasskeyItem {
  credentialId: string;
  name: string;
  createdAt: string;
  isBackedUp: boolean;
}

// ── Helpers ─────────────────────────────────────────────────────

/** Convert ArrayBuffer | Array | Uint8Array to base64url string */
function toBase64Url(o: unknown): string | undefined {
  if (!o) return undefined;

  let bytes: Uint8Array;
  if (Array.isArray(o)) {
    bytes = Uint8Array.from(o);
  } else if (o instanceof ArrayBuffer) {
    bytes = new Uint8Array(o);
  } else if (o instanceof Uint8Array) {
    bytes = o;
  } else {
    return undefined;
  }

  let str = '';
  for (let i = 0; i < bytes.byteLength; i++) {
    str += String.fromCharCode(bytes[i]);
  }
  return btoa(str).replace(/\+/g, '-').replace(/\//g, '_').replace(/=*$/g, '');
}

/** Serialize a PublicKeyCredential to JSON, with manual base64url conversion
 *  to work around password managers that don't implement toJSON(). */
function serializeCredential(cred: any): string {
  const response = cred.response as any;
  const isAttestation = 'attestationObject' in response;

  const respObj: Record<string, unknown> = {
    clientDataJSON: toBase64Url(response.clientDataJSON),
  };

  if (isAttestation) {
    respObj.attestationObject = toBase64Url(response.attestationObject);
    respObj.transports = response.getTransports?.() ?? undefined;
  } else {
    respObj.authenticatorData = toBase64Url(response.authenticatorData);
    respObj.signature = toBase64Url(response.signature);
    respObj.userHandle = toBase64Url(response.userHandle);
  }

  return JSON.stringify({
    type: cred.type,
    id: cred.id,
    rawId: toBase64Url(cred.rawId),
    authenticatorAttachment: cred.authenticatorAttachment,
    clientExtensionResults: cred.getClientExtensionResults(),
    response: respObj,
  });
}

/** Typed wrapper for PublicKeyCredential static methods that may not be typed */
function parseCreationOptions(json: unknown): PublicKeyCredentialCreationOptions {
  return (PublicKeyCredential as any).parseCreationOptionsFromJSON(json);
}
function parseRequestOptions(json: unknown): PublicKeyCredentialRequestOptions {
  return (PublicKeyCredential as any).parseRequestOptionsFromJSON(json);
}

// ── Register components via alpine:init event ──────────────────
// Following the canonical Alpine.js pattern: register data components
// inside alpine:init so they're available before Alpine.start() scans the DOM.
// The app.js script must load BEFORE alpine.js (see _Layout.cshtml).
document.addEventListener('alpine:init', () => {

// ── Passkey Sign-In Component ───────────────────────────────────

Alpine.data('passkeySignIn', function () {
  const state = {
    email: '' as string,
    signingIn: false,
    error: '',

    init(this: any) {
      // Try conditional UI if the browser supports it
      if (typeof window !== 'undefined' && window.PublicKeyCredential) {
        const pk = window.PublicKeyCredential as any;
        if (typeof pk.isConditionalMediationAvailable === 'function') {
          this.tryConditionalUi();
        }
      }
    },

    async signIn(this: any) {
      if (!this.email) {
        this.error = 'Enter your email address first';
        return;
      }

      this.signingIn = true;
      this.error = '';

      try {
        const optionsRes = await fetch(
          `/api/passkeys/request-options?username=${encodeURIComponent(this.email)}`,
          { method: 'POST' }
        );

        if (!optionsRes.ok) throw new Error('No passkey found for this account');

        const optionsJson = await optionsRes.json();
        const options = parseRequestOptions(optionsJson);

        const cred = await navigator.credentials.get({ publicKey: options });
        if (!cred) throw new Error('No credential returned');

        const signInRes = await fetch('/api/passkeys/sign-in', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ credentialJson: serializeCredential(cred) }),
        });

        if (signInRes.ok) {
          const params = new URLSearchParams(window.location.search);
          window.location.href = params.get('ReturnUrl') ?? '/';
        } else if (signInRes.status === 401) {
          this.error = 'Passkey authentication failed — wrong credential?';
        } else {
          const err = await signInRes.json().catch(() => ({}));
          this.error = err.error ?? 'Sign-in failed';
        }
      } catch (e: any) {
        if (e.name !== 'AbortError') {
          this.error = e.message || 'Sign-in failed';
        }
      } finally {
        this.signingIn = false;
      }
    },

    async tryConditionalUi(this: any) {
      try {
        const pk = window.PublicKeyCredential as any;
        const available = await pk.isConditionalMediationAvailable();
        if (!available) return;

        const optionsRes = await fetch('/api/passkeys/request-options', { method: 'POST' });
        if (!optionsRes.ok) return;

        const optionsJson = await optionsRes.json();
        const options = parseRequestOptions(optionsJson);

        const cred = await navigator.credentials.get({
          publicKey: options,
          mediation: 'conditional' as CredentialMediationRequirement,
        });

        if (!cred) return;

        const signInRes = await fetch('/api/passkeys/sign-in', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ credentialJson: serializeCredential(cred) }),
        });

        if (signInRes.ok) {
          window.location.href = '/';
        }
      } catch {
        // Conditional UI fails silently
      }
    },
  };

  return state;
});

// ── Passkey Management Component ────────────────────────────────

Alpine.data('passkeyManage', function () {
  const state = {
    passkeys: [] as PasskeyItem[],
    loading: true,
    error: '',
    adding: false,
    newPasskeyName: '',

    init(this: any) {
      this.load();
    },

    async load(this: any) {
      this.loading = true;
      this.error = '';
      try {
        const res = await fetch('/api/passkeys');
        if (!res.ok) throw new Error('Failed to load passkeys');
        this.passkeys = await res.json();
      } catch (e: any) {
        this.error = e.message || 'Error loading passkeys';
      } finally {
        this.loading = false;
      }
    },

    async startAdd(this: any) {
      this.adding = true;
      this.error = '';

      try {
        const optionsRes = await fetch('/api/passkeys/creation-options', { method: 'POST' });
        if (!optionsRes.ok) throw new Error('Failed to get creation options — are you signed in?');

        const optionsJson = await optionsRes.json();
        const options = parseCreationOptions(optionsJson);

        const cred = await navigator.credentials.create({ publicKey: options });
        if (!cred) throw new Error('No credential returned');

        const attestRes = await fetch('/api/passkeys/attestation', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            credentialJson: serializeCredential(cred),
            name: this.newPasskeyName || undefined,
          }),
        });

        if (!attestRes.ok) {
          const err = await attestRes.json().catch(() => ({ error: 'Attestation failed' }));
          throw new Error(err.error || 'Failed to store passkey');
        }

        this.newPasskeyName = '';
        await this.load();
      } catch (e: any) {
        if (e.name !== 'AbortError') {
          this.error = e.message || 'Failed to create passkey';
        }
      } finally {
        this.adding = false;
      }
    },

    async deletePasskey(this: any, credentialId: string) {
      if (!confirm('Permanently remove this passkey?')) return;

      this.error = '';
      try {
        const res = await fetch(`/api/passkeys/${credentialId}`, { method: 'DELETE' });
        if (!res.ok) throw new Error('Failed to remove');
        await this.load();
      } catch (e: any) {
        this.error = e.message || 'Error removing passkey';
      }
    },

    async renamePasskey(this: any, credentialId: string, currentName: string) {
      const newName = prompt('Rename passkey:', currentName);
      if (!newName || newName === currentName) return;

      this.error = '';
      try {
        const res = await fetch(`/api/passkeys/${credentialId}/name`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: newName }),
        });
        if (!res.ok) throw new Error('Failed to rename');
        await this.load();
      } catch (e: any) {
        this.error = e.message || 'Error renaming passkey';
      }
    },

    formatDate(dateStr: string): string {
      return new Date(dateStr).toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    },
  };

  return state;
});

}); // end alpine:init

