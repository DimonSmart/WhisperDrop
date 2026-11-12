#include <stdio.h>
#include <stddef.h>
#include "transcribe.h"

/* Independent field offsets from the pinned C compiler/header, no native-library dependency. */
#define BEGIN(C, M) printf("{\"Type\":\"" #M "\",\"Size\":%zu,\"Align\":%zu,\"Offsets\":{", sizeof(struct C), _Alignof(struct C))
#define FIELD(C, M, N) printf("\"" #M "\":%zu,", offsetof(struct C, N))
#define END(C, M, N) printf("\"" #M "\":%zu}}", offsetof(struct C, N))
int main(void) {
    printf("[");
    BEGIN(transcribe_model_load_params, LoadParams);
    FIELD(transcribe_model_load_params, Size, struct_size); FIELD(transcribe_model_load_params, Backend, backend);
    FIELD(transcribe_model_load_params, Device, device); END(transcribe_model_load_params, NoMmap, no_mmap);
    printf(","); BEGIN(transcribe_session_params, SessionParams);
    FIELD(transcribe_session_params, Size, struct_size); FIELD(transcribe_session_params, Threads, n_threads);
    FIELD(transcribe_session_params, KvType, kv_type); END(transcribe_session_params, Context, n_ctx);
    printf(","); BEGIN(transcribe_run_params, RunParams);
    FIELD(transcribe_run_params, Size, struct_size); FIELD(transcribe_run_params, Task, task);
    FIELD(transcribe_run_params, Timestamps, timestamps); FIELD(transcribe_run_params, Pnc, pnc);
    FIELD(transcribe_run_params, Itn, itn); FIELD(transcribe_run_params, Diarize, diarize);
    FIELD(transcribe_run_params, Language, language); FIELD(transcribe_run_params, TargetLanguage, target_language);
    FIELD(transcribe_run_params, KeepSpecialTags, keep_special_tags); FIELD(transcribe_run_params, Family, family);
    FIELD(transcribe_run_params, SpecDrafts, spec_k_drafts); FIELD(transcribe_run_params, Vocabulary, vocabulary);
    FIELD(transcribe_run_params, VocabularyCount, n_vocabulary); FIELD(transcribe_run_params, Prompt, prompt);
    END(transcribe_run_params, Prefix, prefix);
    printf(","); BEGIN(transcribe_timings, Timings);
    FIELD(transcribe_timings, Size, struct_size); FIELD(transcribe_timings, Load, load_ms);
    FIELD(transcribe_timings, Mel, mel_ms); FIELD(transcribe_timings, Encode, encode_ms); END(transcribe_timings, Decode, decode_ms);
    printf(","); BEGIN(transcribe_segment, Segment);
    FIELD(transcribe_segment, Size, struct_size); FIELD(transcribe_segment, Start, t0_ms); FIELD(transcribe_segment, End, t1_ms);
    FIELD(transcribe_segment, FirstWord, first_word); FIELD(transcribe_segment, Words, n_words);
    FIELD(transcribe_segment, FirstToken, first_token); FIELD(transcribe_segment, Tokens, n_tokens);
    FIELD(transcribe_segment, Text, text); END(transcribe_segment, Speaker, speaker_id);
    printf(","); BEGIN(transcribe_word, Word);
    FIELD(transcribe_word, Size, struct_size); FIELD(transcribe_word, Start, t0_ms); FIELD(transcribe_word, End, t1_ms);
    FIELD(transcribe_word, Segment, seg_index); FIELD(transcribe_word, FirstToken, first_token);
    FIELD(transcribe_word, Tokens, n_tokens); END(transcribe_word, Text, text);
    printf(","); BEGIN(transcribe_token, Token);
    FIELD(transcribe_token, Size, struct_size); FIELD(transcribe_token, Id, id); FIELD(transcribe_token, Probability, p);
    FIELD(transcribe_token, Start, t0_ms); FIELD(transcribe_token, End, t1_ms); FIELD(transcribe_token, Segment, seg_index);
    FIELD(transcribe_token, Word, word_index); END(transcribe_token, Text, text);
    printf(","); BEGIN(transcribe_backend_init_params, BackendParams);
    FIELD(transcribe_backend_init_params, Size, struct_size); FIELD(transcribe_backend_init_params, Directory, artifact_dir);
    END(transcribe_backend_init_params, Allowed, allowed_backends);
    printf(","); BEGIN(transcribe_device_info, DeviceInfo);
    FIELD(transcribe_device_info, Size, struct_size); FIELD(transcribe_device_info, Name, name);
    FIELD(transcribe_device_info, Description, description); FIELD(transcribe_device_info, Kind, kind);
    FIELD(transcribe_device_info, Id, device_id); FIELD(transcribe_device_info, MemoryTotal, memory_total);
    FIELD(transcribe_device_info, MemoryFree, memory_free); END(transcribe_device_info, Type, device_type);
    printf("]\n"); return 0;
}
