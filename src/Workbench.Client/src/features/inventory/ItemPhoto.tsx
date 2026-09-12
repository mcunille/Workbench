import { useEffect, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getPhoto } from '../../api/items';
import { Icon } from '../../Icon';
export function ItemPhoto({
  url,
  name,
  onAuthLost,
  interactive = false,
}: {
  url?: string;
  name: string;
  onAuthLost(): void;
  interactive?: boolean;
}) {
  const [image, setImage] = useState<{ url: string; objectUrl: string }>();
  const [failedUrl, setFailedUrl] = useState<string>();
  const [recoveryLossUrl, setRecoveryLossUrl] = useState<string>();
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    let objectUrl: string | undefined;
    void getPhoto(url, controller.signal).then(
      (blob) => {
        if (controller.signal.aborted) return;
        objectUrl = URL.createObjectURL(blob);
        setImage({ url, objectUrl });
        setFailedUrl(undefined);
        setRecoveryLossUrl(undefined);
      },
      (error) => {
        if (controller.signal.aborted) return;
        if (
          error instanceof ApiError &&
          (error.status === 401 || error.status === 403)
        )
          onAuthLost();
        setFailedUrl(url);
        setRecoveryLossUrl(error instanceof ApiError && error.status === 410 ? url : undefined);
      },
    );
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [url, retry, onAuthLost]);
  return (
    <span className="photo-placeholder item-photo" data-photo={url ? 'present' : 'absent'}>
      {url && image?.url === url ? (
        <img
          src={image.objectUrl}
          alt={`Photograph of ${name}`}
          onError={() => {
            setImage(undefined);
            setFailedUrl(url);
          }}
        />
      ) : failedUrl && failedUrl === url ? (
        <span role="status">
          {recoveryLossUrl === url
            ? 'This photograph could not be recovered. Replace it with another copy.'
            : 'Photograph unavailable'}{' '}
          {interactive && recoveryLossUrl !== url ? (
            <button
              type="button"
              onClick={(event) => {
                event.preventDefault();
                event.stopPropagation();
                setRetry((value) => value + 1);
              }}
            >
              Retry photograph
            </button>
          ) : null}
        </span>
      ) : (
        <Icon name="image" />
      )}
    </span>
  );
}
