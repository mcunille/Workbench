"""Mux real Playwright footage with local narration and burned-in captions."""

import argparse
import json
from pathlib import Path
import subprocess
import textwrap


def timestamp(seconds: float) -> str:
    milliseconds = round(seconds * 1000)
    hours, remainder = divmod(milliseconds, 3600000)
    minutes, remainder = divmod(remainder, 60000)
    seconds, milliseconds = divmod(remainder, 1000)
    return f"{hours:02}:{minutes:02}:{seconds:02},{milliseconds:03}"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    parser.add_argument("--scenario", choices=["h1", "h2", "h3"], default="h1")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    source = root / f"artifacts/{args.scenario}-video"
    destination = root / f"docs/demos/{args.scenario}"
    destination.mkdir(parents=True, exist_ok=True)
    timeline = json.loads((source / "timeline.json").read_text(encoding="utf-8"))
    durations = json.loads((source / "durations.json").read_text(encoding="utf-8-sig"))
    command = [str(args.ffmpeg.resolve()), "-y", "-i", str(source / "walkthrough.webm")]
    filters = []
    subtitles = []
    transcript = [f"# {args.scenario.upper()} narrated walkthrough", "", "Narration: Microsoft Zira (synthetic voice).", ""]
    for index, scene in enumerate(timeline, 1):
        command += ["-i", str(source / f"{scene['id']}.wav")]
        delay = round((scene["start"] + 0.3) * 1000)
        filters.append(f"[{index}:a]adelay={delay}:all=1[a{index}]")
        words = scene["text"].split()
        # Short readable captions, proportionally timed across each narrated scene.
        for first in range(0, len(words), 16):
            last = min(first + 16, len(words))
            start = scene["start"] + 0.3 + durations[scene["id"]] * first / len(words)
            end = scene["start"] + 0.3 + durations[scene["id"]] * last / len(words)
            caption = textwrap.fill(" ".join(words[first:last]), width=85)
            subtitles.append(f"{len(subtitles) + 1}\n{timestamp(start)} --> {timestamp(end)}\n{caption}\n")
        transcript += [f"## {timestamp(scene['start']).split(',')[0]} — {scene['title']}", "", scene["text"], ""]
    (destination / "captions.srt").write_text("\n".join(subtitles), encoding="utf-8")
    (destination / "transcript.md").write_text("\n".join(transcript), encoding="utf-8")
    audio = "".join(f"[a{i}]" for i in range(1, len(timeline) + 1))
    filters.append(f"{audio}amix=inputs={len(timeline)}:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[audio]")
    # Center the actual phone viewport, replacing the recorder's unused gray canvas.
    mobile = next(scene for scene in timeline if scene["id"] == "mobile")
    filters.append("[0:v]split[desktop][phone]")
    filters.append("[phone]crop=390:844:0:0,pad=1280:900:445:28:color=0x181818[mobile]")
    filters.append(f"[desktop][mobile]overlay=0:0:enable='between(t,{mobile['start'] + 0.3},{mobile['end'] - 0.1})'[framed]")
    # Add a dedicated caption band below the application viewport.
    filters.append("[framed]pad=1280:1000:0:0:color=0x181818,subtitles=captions.srt:force_style='FontName=Arial,FontSize=7,PrimaryColour=&H00FFFFFF,Outline=0,Shadow=0,MarginV=7'[video]")
    command += ["-filter_complex", ";".join(filters), "-map", "[video]", "-map", "[audio]",
                "-t", str(timeline[-1]["end"]), "-c:v", "libx264", "-preset", "medium",
                "-crf", "27", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k",
                "-movflags", "+faststart", str(destination / f"{args.scenario}-walkthrough.mp4")]
    subprocess.run(command, cwd=destination, check=True)
    # Decode the complete result to detect corrupt frames or an invalid audio stream.
    subprocess.run([str(args.ffmpeg.resolve()), "-v", "error", "-i",
                    str(destination / f"{args.scenario}-walkthrough.mp4"), "-f", "null", "-"], check=True)


if __name__ == "__main__":
    main()
