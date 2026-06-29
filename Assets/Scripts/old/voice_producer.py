
import pickle

from audio_prepare import audio_to_file

import json
# ref_radios={"happy":"いえ、見えてましたよ。みなさんがいるの。わたし、目がいいので","normal":"悲しまないでください。わたしも、悲しくなってしまいます","shy":"すみません、夏生さんを放り出してしまって、足しっかくです。","sad":"わたしはやはりポンコツです。本来、それはわたしの役目なのに","angry":"間違いありません。知性の欠片も感じない、ジャカジャカとうるさいだけの音楽です"}

#
# ref_radios={"happy":"いえ、見えてましたよ。みなさんがいるの。わたし、目がいいので","normal":"悲しまないでください。わたしも、悲しくなってしまいます","shy":"すみません、夏生さんを放り出してしまって、足しっかくです。","sad":"わたしはやはりポンコツです。本来、それはわたしの役目なのに","赌气":"間違いありません。知性の欠片も感じない、ジャカジャカとうるさいだけの音楽です"}



def get_radio(text,target_addr,ref_name,ai_tool,model_name,lan="jp"):
    print(text)
    if lan=="jp":
        lan="ja"
    if lan=="cn":
        lan="zh"
    dt={
        "text":text,                   # str.(required) text to be synthesized
        "text_lang":lan,               # str.(required) language of the text to be synthesized
        "ref_audio_name":ref_name,
        "prompt_text":"",
          # str.(optional) prompt text for the reference audio
        "prompt_lang": lan,            # str.(required) language of the prompt text for the reference audio
        "top_k": 15,                   # int. top k sampling
        "top_p": 1,                   # float. top p sampling
        "temperature": 1,             # float. temperature for sampling
        "text_split_method": "cut1",  # str. text split method, see text_segmentation_method.py for details.
        "model_name":model_name,

    }

    data = ai_tool.voice_produce(dt)


    if isinstance(data, dict):
        error = data.get("error", "unknown error")
        detail = data.get("detail", "")
        raise RuntimeError(f"voice_produce failed: {error} {detail}".strip())

    if not isinstance(data, (bytes, bytearray)):
        raise TypeError(f"voice_produce returned unsupported type: {type(data).__name__}")

    try:
        data = pickle.loads(data)
    except pickle.UnpicklingError as exc:
        preview = bytes(data[:200]).decode("utf-8", errors="replace")
        raise RuntimeError(f"voice_produce returned invalid pickle data: {preview}") from exc

    audio_to_file(data[-1][0],data[-1][1],target_addr)
    #
    #
    # data = pickle.loads(r.content)
    # # wave=r.content
    # # #
    # # # #
    # # #
    # # #
    # # # with open(r.content,"rb") as f:
    # # #     data=f.read()
    # # with open(target_addr,"wb") as f:
    # #     f.write(wave)
    #
    # sf.write(target_addr
    #          ,data[-1][1],data[-1][0])
    # r.close()
#
# get_radio("こんにちは、夏生さん","happy","voice.wav")
