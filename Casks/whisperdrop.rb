cask "whisperdrop" do
  arch arm: "arm64", intel: "x64"

  version "0.0.0"

  sha256 arm: :no_check,
         intel: :no_check

  url "https://github.com/DimonSmart/WhisperDrop/releases/download/v#{version}/WhisperDrop-v#{version}-osx-#{arch}-app.zip"

  name "WhisperDrop"
  desc "Local desktop speech-to-text application powered by Whisper"
  homepage "https://github.com/DimonSmart/WhisperDrop"

  app "WhisperDrop.app"

  caveats <<~EOS
    Bootstrap Cask metadata. It is replaced automatically after the first release.
    WhisperDrop is currently unsigned and not notarized.
  EOS
end
