"""Range-owned transcripts. Only a drained result stream can commit a turn."""
import copy
import math
import uuid


class CommandAssembler:
    MAX_TURNS = 3
    MAX_SEGMENTS = 256
    MAX_TEXT = 16000

    def __init__(self):
        self.reset(0)

    def reset(self, generation):
        self.generation = generation
        self.sequence = 0
        self.turns = {}
        self.committed_before = 0

    @staticmethod
    def number(value):
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
            raise ValueError('Invalid speech timestamp')
        return value

    def consume(self, event):
        if event.get('protocol_version') != 1:
            raise ValueError('Unsupported speech protocol')
        if event.get('speech_generation') != self.generation:
            return None
        sequence = event.get('event_sequence')
        if type(sequence) is not int or sequence <= self.sequence:
            raise ValueError('Speech events arrived out of order')
        self.sequence = sequence
        kind, identity = event.get('type'), event.get('turn_id')
        if kind == 'turn_started':
            start = self.number(event['start_ms'])
            if (not isinstance(identity, str) or not identity or len(identity) > 80
                    or identity in self.turns or len(self.turns) >= self.MAX_TURNS
                    or start < self.committed_before):
                raise ValueError('Invalid or overflowing speech turn')
            self.turns[identity] = {'id': uuid.uuid4().hex, 'start': start, 'segments': [],
                                    'cutoff': None, 'last_result': 0, 'watermark': start}
        elif kind in {'transcript_update', 'turn_boundary', 'finalize_completed'}:
            turn = self.turns.get(identity)
            if turn is None:
                raise ValueError('Speech result has no current audio owner')
            if kind == 'turn_boundary':
                cutoff = self.number(event['cutoff_ms'])
                if turn['cutoff'] is not None or cutoff < turn['start']:
                    raise ValueError('Invalid speech cutoff')
                turn.update(cutoff=cutoff, boundary=event.get('reason'),
                            last_speech_ms=event.get('last_speech_ms'))
            elif kind == 'transcript_update':
                start, end = map(self.number, event['audio_range_ms'])
                if start < turn['start'] or end < start or (turn['cutoff'] is not None and end > turn['cutoff'] + 1):
                    raise ValueError('Transcript crosses its command boundary')
                text = event.get('text')
                alternatives = event.get('alternatives', [])
                if not isinstance(text, str) or not isinstance(alternatives, list) or any(not isinstance(a, str) for a in alternatives):
                    raise ValueError('Invalid transcript text')
                segments = turn['segments']
                overlap = [s for s in segments if start < s['audio_range_ms'][1] and end > s['audio_range_ms'][0]]
                if any(s['is_final'] and (s['audio_range_ms'] != [start, end] or s['text'] != text) for s in overlap):
                    raise ValueError('Recognizer revised a finalized audio range')
                # Revisions own intervals, not text. Identical intentional commands survive.
                segments = [s for s in segments if s not in overlap]
                segment = {'audio_range_ms': [start, end], 'text': text,
                           'alternatives': list(dict.fromkeys([text, *alternatives]))[:3],
                           'confidence_spans': copy.deepcopy(event.get('confidence_spans', [])),
                           'is_final': event.get('is_final') is True}
                segments.append(segment)
                segments.sort(key=lambda s: s['audio_range_ms'])
                if len(segments) > self.MAX_SEGMENTS or sum(len(s['text']) + sum(map(len, s['alternatives'])) for s in segments) > self.MAX_TEXT:
                    raise ValueError('Command too long; discarded without truncating')
                turn['segments'] = segments
                turn['last_result'] = sequence
                turn['watermark'] = max(turn['watermark'], self.number(event['finalized_before_ms']))
            else:
                cutoff = turn['cutoff']
                # finalize() alone is NOT a consumer barrier. The helper finishes
                # this turn's analyzer and awaits its result iterator before this event.
                if (cutoff is None or event.get('cutoff_ms') != cutoff
                        or event.get('results_drained') is not True
                        or event.get('last_result_sequence') != turn['last_result']
                        or self.number(event['finalized_before_ms']) < cutoff):
                    raise ValueError('Speech finalization has no result-consumer barrier')
                if any(other['start'] < turn['start'] for key, other in self.turns.items() if key != identity):
                    raise ValueError('Speech turns finalized out of order')
                del self.turns[identity]
                self.committed_before = cutoff
                text = ' '.join(s['text'].strip() for s in turn['segments']).strip()
                if not text:
                    return {'empty': True, 'audio_range_ms': [turn['start'], cutoff]}
                segments = copy.deepcopy(turn['segments'])
                for segment in segments:
                    if not segment['is_final']:
                        segment['finalization_source'] = 'drained_result_stream'
                        segment['is_final'] = True
                return {'id': turn['id'], 'speech_generation': self.generation,
                        'audio_range_ms': [turn['start'], cutoff], 'text': text,
                        'segments': segments, 'boundary': turn.get('boundary'),
                        'last_speech_ms': turn.get('last_speech_ms')}
        return None

    def preview(self):
        return [{'turnId': key, 'text': ' '.join(s['text'].strip() for s in turn['segments']),
                 'finalizing': turn['cutoff'] is not None, 'segments': copy.deepcopy(turn['segments'])}
                for key, turn in self.turns.items()]
