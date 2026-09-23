/**
 * Minimal WHEP client: plays a MediaMTX WebRTC path into a <video>. WHEP is one HTTP POST carrying an SDP offer,
 * the answer comes back in the response body, and the media then flows peer to peer with the server.
 */
export class WhepPlayer {
  private pc?: RTCPeerConnection;
  private stopped = false;

  constructor(private readonly video: HTMLVideoElement) {}

  async play(whepUrl: string): Promise<void> {
    this.stop();
    this.stopped = false;
    const pc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] });
    this.pc = pc;

    // Set as properties, not template attributes: Chrome's autoplay policy only trusts the muted *property*, and a
    // rejected play() leaves a black tile with no error anywhere visible.
    this.video.muted = true;
    this.video.autoplay = true;
    this.video.playsInline = true;

    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.addTransceiver('audio', { direction: 'recvonly' });
    pc.ontrack = ev => {
      if (this.stopped) return;
      if (this.video.srcObject !== ev.streams[0]) this.video.srcObject = ev.streams[0];
      this.video.play().catch(err => console.warn('live video play() rejected:', err?.message));
    };

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await this.waitForIceGathering(pc);

    const res = await fetch(whepUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: pc.localDescription!.sdp,
    });
    if (!res.ok) throw new Error(`WHEP ${res.status}`);
    const answer = await res.text();
    if (this.stopped) return;
    await pc.setRemoteDescription({ type: 'answer', sdp: answer });
  }

  stop(): void {
    this.stopped = true;
    this.pc?.close();
    this.pc = undefined;
    this.video.srcObject = null;
  }

  private waitForIceGathering(pc: RTCPeerConnection): Promise<void> {
    if (pc.iceGatheringState === 'complete') return Promise.resolve();
    return new Promise(resolve => {
      const done = () => { pc.removeEventListener('icegatheringstatechange', check); resolve(); };
      const check = () => { if (pc.iceGatheringState === 'complete') done(); };
      pc.addEventListener('icegatheringstatechange', check);
      setTimeout(done, 1500);   // local candidates are enough for a LAN server; do not wait on STUN forever
    });
  }
}
