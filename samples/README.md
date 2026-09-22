# samples/

Put short demo clips here (they are git-ignored). Anything ffmpeg can decode works: mp4, ts, mkv, or an RTSP URL.

Public traffic footage that works well for a demo:

- Any dashcam or junction clip from Pexels or Pixabay (search "traffic", "street", "pedestrians").
- The 60 second clip used in the README was cut from a junction camera with:

```
ffmpeg -ss 0 -t 60 -i full.mp4 -c copy samples/junction_60s.mp4
```
